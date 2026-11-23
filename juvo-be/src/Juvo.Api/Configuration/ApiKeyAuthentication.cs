using System.Security.Claims;
using System.Text.Encodings.Web;
using Juvo.Application.Integrations;
using Juvo.Shared.Results;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Juvo.Api.Configuration;

internal sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authentication of the public API (FR-AF05): the <c>X-Api-Key</c> header is looked up by its public prefix, compared with the stored SHA-256 hash in constant time, and
/// checked for enabled and expiry. It is a separate scheme from the staff and customer JWT, so a staff token never opens the public API and a key never opens the staff API.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyAuthenticator authenticator)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string KeyIdClaim = "api_key_id";
    public const string BranchClaim = "api_branch";
    public const string ScopeClaim = "api_scope";
    public const string LimitClaim = "api_limit";
    private const string FailureItem = "Juvo.PublicApi.Failure";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(presented))
        {
            return AuthenticateResult.NoResult();
        }

        var check = await authenticator.CheckAsync(presented.Trim(), Context.Connection.RemoteIpAddress?.ToString(), Context.RequestAborted);
        if (!check.Ok)
        {
            Context.Items[FailureItem] = check.FailureCode;
            return AuthenticateResult.Fail(check.FailureCode!);
        }

        var claims = new List<Claim> { new(KeyIdClaim, check.KeyId!.Value.ToString()) };
        claims.AddRange(check.Scopes.Select(s => new Claim(ScopeClaim, s)));
        if (check.BranchId is { } branch)
        {
            claims.Add(new Claim(BranchClaim, branch.ToString()));
        }

        if (check.RateLimitPerMinute is { } limit)
        {
            claims.Add(new Claim(LimitClaim, limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items[FailureItem] as string ?? ErrorCodes.PublicApi.InvalidKey;
        var title = code switch
        {
            ErrorCodes.PublicApi.KeyDisabled => "This API key is disabled.",
            ErrorCodes.PublicApi.KeyExpired => "This API key has expired.",
            _ => "A valid API key is required in the X-Api-Key header.",
        };
        Response.Headers.WWWAuthenticate = "ApiKey realm=\"Juvo BD public API\"";
        await WriteAsync(StatusCodes.Status401Unauthorized, title, code);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteAsync(StatusCodes.Status403Forbidden, "This API key does not have the scope this endpoint needs.", ErrorCodes.PublicApi.ScopeMissing);

    private async Task WriteAsync(int status, string title, string code)
    {
        Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        await Context.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext { HttpContext = Context, ProblemDetails = problem });
    }

    /// <summary>The caller built from the authenticated key (after authorization succeeded).</summary>
    public static ApiCaller Caller(ClaimsPrincipal user)
    {
        var scopes = user.FindAll(ScopeClaim).Select(c => c.Value).ToList();
        Guid? branch = Guid.TryParse(user.FindFirstValue(BranchClaim), out var b) ? b : null;
        return new ApiCaller(Guid.Parse(user.FindFirstValue(KeyIdClaim)!), branch, scopes.Contains(ApiKeyScopes.PiiRead));
    }
}

/// <summary>Counts the request against the key's per-minute limit (key override, else the setting <c>PublicApi.RateLimitPerMinute</c>); over the limit it answers 429 with Retry-After.</summary>
internal sealed class PublicApiRateLimitFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(http.User.FindFirstValue(ApiKeyAuthenticationHandler.KeyIdClaim), out var keyId))
        {
            return await next(context);
        }

        var limit = int.TryParse(http.User.FindFirstValue(ApiKeyAuthenticationHandler.LimitClaim), out var own)
            ? own
            : await http.RequestServices.GetRequiredService<Juvo.Application.Settings.ISettingsReader>().GetIntAsync(PublicApiSettingKeys.RateLimitPerMinute, http.RequestAborted);
        var decision = http.RequestServices.GetRequiredService<ApiKeyRateLimiter>().Hit(keyId, limit);
        http.Response.Headers["X-RateLimit-Limit"] = decision.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-RateLimit-Remaining"] = decision.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (decision.Allowed)
        {
            return await next(context);
        }

        http.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Http.ResultHttpExtensions.ToProblem(new Error(ErrorCodes.PublicApi.RateLimited, "Too many requests for this API key. Wait and try again.", ErrorType.TooManyRequests));
    }
}
