using System.Text.RegularExpressions;

namespace Juvo.Api.Http;

/// <summary>
/// Gives every request a correlation id (from a valid incoming <c>X-Correlation-Id</c> or a new one), returns it in the
/// response header and puts it in the logging scope so one id ties together logs, audit rows and error responses.
/// </summary>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var id = ValidId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.Items[HttpCurrentUser.CorrelationItemKey] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = id;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await next(context);
        }
    }

    public static string? Get(HttpContext context) => context.Items[HttpCurrentUser.CorrelationItemKey] as string;
}
