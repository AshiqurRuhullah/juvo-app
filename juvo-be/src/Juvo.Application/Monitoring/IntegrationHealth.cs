using System.Collections.Concurrent;
using Juvo.Shared.Results;

namespace Juvo.Application.Monitoring;

public enum IntegrationStatus
{
    Healthy = 1,
    Degraded = 2,
    Down = 3,
    Disabled = 4,
    Sandbox = 5,
}

/// <summary>Marks an adapter that never calls a real provider (the sandbox adapters). The integration health shows such a slot as Sandbox.</summary>
public interface ISandboxAdapter
{
}

/// <summary>An adapter wrapped by a recording decorator; lets the health service look at the real adapter underneath.</summary>
public interface IWrappedAdapter
{
    object Inner { get; }
}

/// <summary>What the process remembers about one integration since it started. The error text is already scrubbed.</summary>
public sealed record IntegrationState(DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, string? LastError, int ConsecutiveFailures, long Successes, long Failures);

/// <summary>Records the outcome of calls to external providers (FR-AF02). One in-memory row per integration key; per process, reset on restart.</summary>
public interface IIntegrationHealthRecorder
{
    void Success(string key);

    void Failure(string key, string? error);

    IntegrationState? Get(string key);
}

public sealed class IntegrationHealthRecorder(TimeProvider clock) : IIntegrationHealthRecorder
{
    private readonly ConcurrentDictionary<string, IntegrationState> _states = new(StringComparer.OrdinalIgnoreCase);

    public void Success(string key)
    {
        var now = clock.GetUtcNow();
        _states.AddOrUpdate(
            key,
            _ => new IntegrationState(now, null, null, 0, 1, 0),
            (_, s) => s with { LastSuccessAt = now, ConsecutiveFailures = 0, Successes = s.Successes + 1 });
    }

    public void Failure(string key, string? error)
    {
        var now = clock.GetUtcNow();
        var text = ErrorScrubber.Scrub(error) ?? "The provider call failed.";
        _states.AddOrUpdate(
            key,
            _ => new IntegrationState(null, now, text, 1, 0, 1),
            (_, s) => s with { LastFailureAt = now, LastError = text, ConsecutiveFailures = s.ConsecutiveFailures + 1, Failures = s.Failures + 1 });
    }

    public IntegrationState? Get(string key) => _states.GetValueOrDefault(key);
}

/// <summary>The rule that turns a slot's flags and recorded outcomes into one status.</summary>
public static class IntegrationStatusRules
{
    /// <summary>Failures in a row from which a live integration counts as Down (one or two in a row is Degraded).</summary>
    public const int DownAfter = 3;

    public static IntegrationStatus Derive(bool enabled, bool sandbox, int consecutiveFailures)
    {
        if (!enabled)
        {
            return IntegrationStatus.Disabled;
        }

        if (sandbox)
        {
            return IntegrationStatus.Sandbox;
        }

        return consecutiveFailures <= 0 ? IntegrationStatus.Healthy : consecutiveFailures >= DownAfter ? IntegrationStatus.Down : IntegrationStatus.Degraded;
    }
}

/// <summary>Counts a provider answer as a failure only when the provider itself failed (not when we refused or the slot is not set up).</summary>
internal static class IntegrationCall
{
    public static async Task<T> TrackAsync<T>(IIntegrationHealthRecorder recorder, string key, Func<Task<T>> call)
        where T : Result
    {
        try
        {
            var result = await call();
            if (result.IsSuccess)
            {
                recorder.Success(key);
            }
            else if (result.Error is { Type: ErrorType.Failure } error && !error.Code.EndsWith(".NOT_CONFIGURED", StringComparison.Ordinal))
            {
                recorder.Failure(key, error.Message);
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            recorder.Failure(key, ex.Message);
            throw;
        }
    }
}
