using System.Diagnostics;
using Juvo.Application.Monitoring;

namespace Juvo.Api.Http;

/// <summary>
/// Times every request and puts method, route template, status and duration in the in-memory ring buffer (FR-AF04). It never stores the real path, the query string, headers or
/// the user, so the buffer holds no personal data. Long-lived real-time connections are left out.
/// </summary>
internal sealed class RequestMonitorMiddleware(RequestDelegate next, RequestMonitor monitor)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(Juvo.Api.Realtime.RealtimeAuth.HubPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            monitor.RecordRequest(context.Request.Method, RouteOf(context), context.Response.StatusCode, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>The route template of the matched endpoint, for example <c>/api/v{version:apiVersion}/orders/{id:guid}</c>; null when nothing matched (a 404 for an unknown address).</summary>
    public static string? RouteOf(HttpContext context) => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
}
