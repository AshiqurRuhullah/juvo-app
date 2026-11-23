using Microsoft.Extensions.Options;

namespace Juvo.Application.Monitoring;

/// <summary>Section <c>Monitoring</c>.</summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>A request slower than this counts as slow (milliseconds).</summary>
    public int SlowRequestMilliseconds { get; set; } = 1000;

    /// <summary>How many recent requests are kept (a ring buffer).</summary>
    public int RequestBufferSize { get; set; } = 500;

    /// <summary>How many recent unhandled errors are kept.</summary>
    public int ErrorBufferSize { get; set; } = 100;
}

/// <summary>One finished request: the route template (never the real path or query string, so no ids or personal data), the status and how long it took.</summary>
public sealed record RequestSample(string Method, string Route, int Status, int DurationMs, DateTimeOffset At);

/// <summary>One unhandled error. The message is scrubbed; there is never a stack trace.</summary>
public sealed record ErrorSample(string? CorrelationId, string Route, string ExceptionType, string? Message, DateTimeOffset At);

public sealed record SlowRoute(string Method, string Route, int Count, int MaxMs, int AverageMs);

/// <summary>
/// A small in-memory ring buffer of the last requests and unhandled errors (FR-AF04). Per process: it is empty after a restart and does not see other servers.
/// Thread safe; a lock is held only for the few instructions that write or copy the buffer.
/// </summary>
public sealed class RequestMonitor(IOptions<MonitoringOptions> options, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly RequestSample?[] _requests = new RequestSample?[Math.Max(1, options.Value.RequestBufferSize)];
    private readonly ErrorSample?[] _errors = new ErrorSample?[Math.Max(1, options.Value.ErrorBufferSize)];
    private int _requestNext;
    private int _errorNext;
    private long _requestTotal;

    public DateTimeOffset StartedAt { get; } = clock.GetUtcNow();

    public int SlowThresholdMs => Math.Max(1, options.Value.SlowRequestMilliseconds);

    public long RequestsSeen
    {
        get
        {
            lock (_gate)
            {
                return _requestTotal;
            }
        }
    }

    public void RecordRequest(string method, string? routeTemplate, int status, TimeSpan duration)
    {
        var sample = new RequestSample(method, string.IsNullOrWhiteSpace(routeTemplate) ? "(no route)" : routeTemplate, status, (int)Math.Min(int.MaxValue, duration.TotalMilliseconds), clock.GetUtcNow());
        lock (_gate)
        {
            _requests[_requestNext] = sample;
            _requestNext = (_requestNext + 1) % _requests.Length;
            _requestTotal++;
        }
    }

    public void RecordError(string? correlationId, string? routeTemplate, Exception exception)
    {
        var sample = new ErrorSample(correlationId, string.IsNullOrWhiteSpace(routeTemplate) ? "(no route)" : routeTemplate, exception.GetType().Name, ErrorScrubber.Scrub(exception.Message), clock.GetUtcNow());
        lock (_gate)
        {
            _errors[_errorNext] = sample;
            _errorNext = (_errorNext + 1) % _errors.Length;
        }
    }

    /// <summary>The kept requests, newest first.</summary>
    public IReadOnlyList<RequestSample> Requests() => Snapshot(_requests, _requestNext);

    /// <summary>The kept unhandled errors, newest first.</summary>
    public IReadOnlyList<ErrorSample> Errors(int limit = 20) => [.. Snapshot(_errors, _errorNext).Take(Math.Max(1, limit))];

    public int SlowCount() => Requests().Count(r => r.DurationMs >= SlowThresholdMs);

    /// <summary>Routes with slow requests among the kept ones, slowest first (by the slowest request on the route).</summary>
    public IReadOnlyList<SlowRoute> SlowestRoutes(int limit = 5) =>
        [.. Requests().Where(r => r.DurationMs >= SlowThresholdMs)
            .GroupBy(r => (r.Method, r.Route))
            .Select(g => new SlowRoute(g.Key.Method, g.Key.Route, g.Count(), g.Max(r => r.DurationMs), (int)g.Average(r => r.DurationMs)))
            .OrderByDescending(r => r.MaxMs).ThenBy(r => r.Route, StringComparer.Ordinal).Take(Math.Max(1, limit))];

    private List<T> Snapshot<T>(T?[] buffer, int next)
        where T : class
    {
        var list = new List<T>(buffer.Length);
        lock (_gate)
        {
            for (var i = 1; i <= buffer.Length; i++)
            {
                if (buffer[(next - i + buffer.Length * 2) % buffer.Length] is { } item)
                {
                    list.Add(item);
                }
            }
        }

        return list;
    }
}
