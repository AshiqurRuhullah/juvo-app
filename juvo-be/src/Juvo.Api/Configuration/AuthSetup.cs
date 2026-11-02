using System.Security.Claims;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Infrastructure.Identity;
using Juvo.Shared.Results;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Juvo.Api.Configuration;

/// <summary>Authorization policies. Endpoints pick one; the permission policies are built on demand from the permission code.</summary>
public static class Policies
{
    /// <summary>Any signed-in person (staff or customer), including limited tokens.</summary>
    public const string Authenticated = "Authenticated";

    /// <summary>Signed-in staff with a full token.</summary>
    public const string Staff = "Staff";

    /// <summary>Signed-in staff including a limited token (password change and two-step setup still pending).</summary>
    public const string StaffLimited = "StaffLimited";

    public const string Customer = "Customer";

    public const string PermissionPrefix = "perm:";
    public const string PermissionAnywherePrefix = "perm-any:";

    /// <summary>The permission must be held in the active branch (or globally).</summary>
    public static string InActiveBranch(string permission) => PermissionPrefix + permission;

    /// <summary>The permission may come from any of the user's assignments. For global features such as users and roles.</summary>
    public static string Anywhere(string permission) => PermissionAnywherePrefix + permission;
}

public static class PermissionEndpointExtensions
{
    /// <summary>Requires a staff member who holds <paramref name="permission"/> in any of their assignments (global features).</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(Policies.Anywhere(permission));

    /// <summary>Requires a staff member who holds <paramref name="permission"/> in the active branch (branch-scoped features).</summary>
    public static TBuilder RequireBranchPermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(Policies.InActiveBranch(permission));
}

internal sealed class PermissionRequirement(string permission, PermissionScope scope) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;

    public PermissionScope Scope { get; } = scope;
}

/// <summary>Checks the permission AND branch scope through <see cref="IPermissionChecker"/>.</summary>
internal sealed class PermissionAuthorizationHandler(IPermissionChecker checker) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (await checker.HasPermissionAsync(requirement.Permission, requirement.Scope))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Turns "perm:orders.order.confirm" and "perm-any:ums.user.view" into policies, so every endpoint can name its permission.</summary>
internal sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        PermissionScope? scope = policyName.StartsWith(Policies.PermissionAnywherePrefix, StringComparison.Ordinal) ? PermissionScope.Anywhere
            : policyName.StartsWith(Policies.PermissionPrefix, StringComparison.Ordinal) ? PermissionScope.ActiveBranch
            : null;
        if (scope is null)
        {
            return await base.GetPolicyAsync(policyName);
        }

        var prefix = scope == PermissionScope.Anywhere ? Policies.PermissionAnywherePrefix : Policies.PermissionPrefix;
        return new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim("stype", "staff")
            .AddRequirements(new NotLimitedRequirement(), new PermissionRequirement(policyName[prefix.Length..], scope.Value))
            .Build();
    }
}

/// <summary>A limited token (password change or two-step setup pending) may not use ordinary endpoints.</summary>
internal sealed class NotLimitedRequirement : IAuthorizationRequirement;

internal sealed class NotLimitedHandler : AuthorizationHandler<NotLimitedRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, NotLimitedRequirement requirement)
    {
        if (!string.Equals(context.User.FindFirstValue("scope"), "limited", StringComparison.Ordinal))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class AuthSetup
{
    public static IServiceCollection AddJuvoAuth(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<SigningKeyProvider, IOptions<AuthOptions>>((bearer, keys, auth) =>
            {
                bearer.MapInboundClaims = false; // keep "sub", "name", "stype" as they are
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = auth.Value.Issuer,
                    ValidateAudience = true,
                    ValidAudience = auth.Value.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = keys.Key,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "name",
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                };
                bearer.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = context =>
                    {
                        if (context.Exception is SecurityTokenExpiredException)
                        {
                            context.HttpContext.Items[ItemKey] = "expired";
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ValidateTokenStateAsync,
                    OnChallenge = WriteChallengeAsync,
                    OnForbidden = WriteForbiddenAsync,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Authenticated, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser())
            .AddPolicy(Policies.StaffLimited, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireClaim("stype", "staff"))
            .AddPolicy(Policies.Staff, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireClaim("stype", "staff").AddRequirements(new NotLimitedRequirement()))
            .AddPolicy(Policies.Customer, p => p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireClaim("stype", "customer"));

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, NotLimitedHandler>();
        return services;
    }

    private const string ItemKey = "Juvo.AuthError";

    /// <summary>
    /// A valid signature is not enough: the account must still exist and be active, and the token must carry the account's
    /// current permission version. Otherwise it is stale (roles changed, password changed, deactivated, signed out everywhere)
    /// and the client is told to refresh.
    /// </summary>
    private static async Task ValidateTokenStateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var states = context.HttpContext.RequestServices.GetRequiredService<IUserStateCache>();
        if (principal is null || !Guid.TryParse(principal.FindFirstValue("sub"), out var id))
        {
            context.Fail("missing subject");
            return;
        }

        var type = principal.FindFirstValue("stype");
        if (type == "staff")
        {
            var state = await states.GetStaffAsync(id, context.HttpContext.RequestAborted);
            if (!state.Exists || !state.IsActive)
            {
                context.HttpContext.Items[ItemKey] = "disabled";
                context.Fail("account disabled");
                return;
            }

            if (!int.TryParse(principal.FindFirstValue("pv"), out var version) || version != state.PermissionVersion)
            {
                context.HttpContext.Items[ItemKey] = "stale";
                context.Fail("stale token");
            }
        }
        else if (type == "customer")
        {
            if (!await states.CustomerExistsAsync(id, context.HttpContext.RequestAborted))
            {
                context.Fail("customer missing");
            }
        }
        else
        {
            context.Fail("unknown subject type");
        }
    }

    private static async Task WriteChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        var reason = context.HttpContext.Items[ItemKey] as string;
        var (code, title) = reason switch
        {
            "expired" or "stale" => (ErrorCodes.Auth.TokenExpired, "The access token expired. Refresh it and try again."),
            "disabled" => (ErrorCodes.Auth.AccountDisabled, "This account is disabled."),
            _ => (ErrorCodes.Auth.Unauthorized, "Authentication is required."),
        };

        context.Response.Headers.WWWAuthenticate = reason is null ? "Bearer" : "Bearer error=\"invalid_token\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, title, code);
    }

    private static Task WriteForbiddenAsync(ForbiddenContext context) =>
        WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, "You do not have permission to do this.", ErrorCodes.Auth.Forbidden);

    private static async Task WriteProblemAsync(HttpContext http, int status, string title, string code)
    {
        http.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = title };
        problem.Extensions["code"] = code;
        await http.RequestServices.GetRequiredService<IProblemDetailsService>().TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
        });
    }
}
