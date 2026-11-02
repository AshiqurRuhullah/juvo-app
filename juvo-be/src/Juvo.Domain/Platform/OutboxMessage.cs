using Juvo.Domain.Common;

namespace Juvo.Domain.Platform;

public enum OutboxStatus
{
    Pending = 0,
    Succeeded = 1,

    /// <summary>All retries used. Stays visible in the job dashboard for a manual retry (FR-AF02).</summary>
    Failed = 2,
}

/// <summary>
/// A message written in the same transaction as the change that caused it (domain events,
/// notifications, webhooks, courier calls) and delivered later by the background worker with retry.
/// </summary>
public sealed class OutboxMessage : Entity, INotAudited
{
    private OutboxMessage()
    {
    }

    public OutboxMessage(string type, string payload, DateTimeOffset now, string? correlationId)
    {
        Type = type;
        Payload = payload;
        NextAttemptAt = now;
        CorrelationId = correlationId;
    }

    /// <summary>Registered message name, for example <c>order.placed</c>.</summary>
    public string Type { get; private set; } = string.Empty;

    public string Payload { get; private set; } = string.Empty;

    public OutboxStatus Status { get; private set; } = OutboxStatus.Pending;

    public int AttemptCount { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    public string? CorrelationId { get; private set; }

    public void MarkSucceeded(DateTimeOffset now)
    {
        Status = OutboxStatus.Succeeded;
        ProcessedAt = now;
        LastError = null;
        AttemptCount++;
    }

    /// <summary>Records a failed attempt. Schedules a retry with exponential backoff, or gives up after <paramref name="maxAttempts"/>.</summary>
    public void MarkFailed(DateTimeOffset now, string error, int maxAttempts, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        AttemptCount++;
        LastError = error.Length > 2000 ? error[..2000] : error;
        if (AttemptCount >= maxAttempts)
        {
            Status = OutboxStatus.Failed;
            ProcessedAt = now;
            return;
        }

        var seconds = Math.Min(maxDelay.TotalSeconds, baseDelay.TotalSeconds * Math.Pow(2, AttemptCount - 1));
        NextAttemptAt = now.AddSeconds(seconds);
    }

    /// <summary>Puts a failed message back in the queue (manual retry from the job dashboard).</summary>
    public void Requeue(DateTimeOffset now)
    {
        Status = OutboxStatus.Pending;
        AttemptCount = 0;
        NextAttemptAt = now;
        ProcessedAt = null;
    }
}
