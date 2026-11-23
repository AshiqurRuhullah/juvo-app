using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;

namespace Juvo.Application.Monitoring;

public sealed record JobListQuery(string? Job, JobStatus? Outcome, DateTimeOffset? From, DateTimeOffset? To, string? Reference);

public sealed record JobRunDto(Guid Id, string JobName, string? Reference, int Attempt, JobStatus Outcome, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int? DurationMs, string? Error, string? CorrelationId);

/// <summary>One row per job name found in the job log. Counts cover the last 24 hours.</summary>
public sealed record JobSummaryDto(string JobName, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, string? LastError, int Runs24h, int Failures24h);

public enum OutboxState
{
    /// <summary>Waiting for its first attempt.</summary>
    Pending = 1,

    /// <summary>Failed before and waiting for the next attempt (backoff).</summary>
    Retrying = 2,

    /// <summary>All attempts used. Needs a manual requeue.</summary>
    DeadLettered = 3,
}

/// <summary><c>State</c> null means every message that is not finished (pending, retrying and dead-lettered).</summary>
public sealed record OutboxListQuery(OutboxState? State, string? Type, DateTimeOffset? From, DateTimeOffset? To);

public sealed record OutboxMessageDto(Guid Id, string Type, OutboxState State, int Attempts, string? LastError, DateTimeOffset NextAttemptAt, DateTimeOffset CreatedAt, DateTimeOffset? GaveUpAt, string? CorrelationId);

public sealed record OutboxSummaryDto(int Pending, int Retrying, int DeadLettered, DateTimeOffset? OldestOpenAt, int? OldestOpenAgeSeconds);

public sealed record RequeueRequest(IReadOnlyList<Guid>? Ids);

public sealed record SkippedItemDto(Guid Id, string Reason);

public sealed record RequeueResultDto(int Requested, int Requeued, IReadOnlyList<SkippedItemDto> Skipped);

public sealed record FailedNotificationQuery(NotificationChannel? Channel, string? Template, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>A failed or dead-lettered message send. The recipient is masked and the text of the message is not shown.</summary>
public sealed record FailedNotificationDto(Guid Id, NotificationChannel Channel, string RecipientMasked, string Template, NotificationStatus Status, int Attempts, string? LastError, DateTimeOffset QueuedAt, DateTimeOffset LastAttemptAt);

/// <summary>What the failure view can show and retry, so a screen can list the kinds and say plainly which have no record.</summary>
public sealed record FailureKindDto(string Kind, bool Recorded, bool CanRetry, string Note);
