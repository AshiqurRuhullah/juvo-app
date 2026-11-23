using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Application.Ums;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Monitoring;

/// <summary>
/// Background jobs and queues for staff (FR-AF02): the job log, the outbox queue with manual requeue, and failed message sends with retry. It reads the tables the workers already
/// write and adds no state of its own. Error texts are scrubbed (no keys, tokens, phone numbers, stack traces). Retries and requeues are audited (who, what, how many).
/// </summary>
public sealed class SystemJobsService(IApplicationDbContext db, ActorContext actors, NotificationService notifications, ICurrentUser currentUser, TimeProvider clock)
{
    public const int MaxBulk = 100;
    public const string OutboxAuditEntity = "OutboxMessage";
    public const string NotificationAuditEntity = "NotificationLog";

    private async Task<(Actor? Actor, Error? Refusal)> RequireAsync(string permission, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return (null, Error.Unauthorized());
        }

        return actor.Access.HasAnywhere(permission) ? (actor, null) : (null, Error.Forbidden());
    }

    // ---- Job runs ------------------------------------------------------------------------------------------

    private static JobRunDto ToDto(JobLog j) =>
        new(j.Id, j.JobName, j.Reference, j.Attempt, j.Status, j.StartedAt, j.FinishedAt, j.FinishedAt is { } f ? (int)Math.Min(int.MaxValue, (f - j.StartedAt).TotalMilliseconds) : null,
            ErrorScrubber.Scrub(j.Error), j.CorrelationId);

    public async Task<Result<PagedResult<JobRunDto>>> ListAsync(PageRequest request, JobListQuery filter, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var query = db.JobLogs.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Job))
        {
            var job = filter.Job.Trim();
            query = query.Where(j => j.JobName == job);
        }

        if (filter.Outcome is { } outcome)
        {
            query = query.Where(j => j.Status == outcome);
        }

        if (filter.From is { } from)
        {
            query = query.Where(j => j.StartedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(j => j.StartedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reference))
        {
            var reference = filter.Reference.Trim();
            query = query.Where(j => j.Reference == reference);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(j => j.JobName.Contains(term) || (j.Reference != null && j.Reference.Contains(term)) || (j.CorrelationId != null && j.CorrelationId.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(j => j.StartedAt).ThenByDescending(j => j.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<JobRunDto>([.. rows.Select(ToDto)], total, page.Page, page.PageSize);
    }

    public async Task<Result<JobRunDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var job = await db.JobLogs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, cancellationToken);
        return job is null ? Error.NotFound(ErrorCodes.Platform.JobNotFound, "Job run not found.") : ToDto(job);
    }

    /// <summary>One row per job name that has ever run, with the last run, last success, last failure and the counts of the last 24 hours.</summary>
    public async Task<Result<IReadOnlyList<JobSummaryDto>>> SummaryAsync(CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        return Result.Success(await SummaryRowsAsync(db, clock.GetUtcNow(), cancellationToken));
    }

    internal static async Task<IReadOnlyList<JobSummaryDto>> SummaryRowsAsync(IApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var since = now.AddHours(-24);
        var rows = await db.JobLogs.AsNoTracking().GroupBy(j => j.JobName).Select(g => new
        {
            Job = g.Key,
            LastRun = g.Max(j => j.StartedAt),
            LastSuccess = g.Where(j => j.Status == JobStatus.Succeeded).Max(j => (DateTimeOffset?)j.StartedAt),
            LastFailure = g.Where(j => j.Status == JobStatus.Failed).Max(j => (DateTimeOffset?)j.StartedAt),
            Runs = g.Count(j => j.StartedAt >= since),
            Failures = g.Count(j => j.StartedAt >= since && j.Status == JobStatus.Failed),
        }).OrderBy(r => r.Job).ToListAsync(cancellationToken);

        var list = new List<JobSummaryDto>(rows.Count);
        foreach (var r in rows)
        {
            string? error = null;
            if (r.LastFailure is { } at)
            {
                error = ErrorScrubber.Scrub(await db.JobLogs.AsNoTracking().Where(j => j.JobName == r.Job && j.Status == JobStatus.Failed && j.StartedAt == at).Select(j => j.Error).FirstOrDefaultAsync(cancellationToken));
            }

            list.Add(new JobSummaryDto(r.Job, r.LastRun, r.LastSuccess, r.LastFailure, error, r.Runs, r.Failures));
        }

        return list;
    }

    /// <summary>Which failures the platform records today and which it can retry. Kinds without a record are named here so nobody assumes they are covered.</summary>
    public async Task<Result<IReadOnlyList<FailureKindDto>>> KindsAsync(CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        IReadOnlyList<FailureKindDto> kinds =
        [
            new("job-runs", true, false, "Every background run is logged. A run is not retried itself: the queue entry it worked on is (see outbox)."),
            new("outbox", true, true, "Messages that failed all attempts stay in the queue and can be requeued, one or up to 100 at a time."),
            new("notifications", true, true, "Failed or dead-lettered SMS, e-mail and WhatsApp sends can be queued again through the normal delivery path."),
            new("courier-bookings", false, false, "A failed booking is answered to the staff member who tried it and is not stored. Book the shipment again from the order. Last provider failure per courier is in the integration health."),
            new("webhooks", false, false, "A refused or failed webhook is answered to the provider (which retries) and is not stored. Last provider failure is in the integration health."),
        ];
        return Result.Success(kinds);
    }

    // ---- Outbox queue --------------------------------------------------------------------------------------

    private static OutboxState StateOf(OutboxMessage m) => m.Status == OutboxStatus.Failed ? OutboxState.DeadLettered : m.AttemptCount > 0 ? OutboxState.Retrying : OutboxState.Pending;

    private static OutboxMessageDto ToDto(OutboxMessage m) =>
        new(m.Id, m.Type, StateOf(m), m.AttemptCount, ErrorScrubber.Scrub(m.LastError), m.NextAttemptAt, m.CreatedAt, m.Status == OutboxStatus.Failed ? m.ProcessedAt : null, m.CorrelationId);

    public async Task<Result<PagedResult<OutboxMessageDto>>> OutboxAsync(PageRequest request, OutboxListQuery filter, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var query = db.OutboxMessages.AsNoTracking().Where(m => m.Status != OutboxStatus.Succeeded);
        query = filter.State switch
        {
            OutboxState.Pending => query.Where(m => m.Status == OutboxStatus.Pending && m.AttemptCount == 0),
            OutboxState.Retrying => query.Where(m => m.Status == OutboxStatus.Pending && m.AttemptCount > 0),
            OutboxState.DeadLettered => query.Where(m => m.Status == OutboxStatus.Failed),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            var type = filter.Type.Trim();
            query = query.Where(m => m.Type == type || m.Type.StartsWith(type));
        }

        if (filter.From is { } from)
        {
            query = query.Where(m => m.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(m => m.CreatedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(m => m.Type.Contains(term) || (m.CorrelationId != null && m.CorrelationId.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<OutboxMessageDto>([.. rows.Select(ToDto)], total, page.Page, page.PageSize);
    }

    public async Task<Result<OutboxMessageDto>> GetOutboxAsync(Guid id, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var message = await db.OutboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        return message is null ? Error.NotFound(ErrorCodes.Platform.MessageNotFound, "Queue message not found.") : ToDto(message);
    }

    public async Task<Result<OutboxSummaryDto>> OutboxSummaryAsync(CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        return await OutboxSummaryRowAsync(db, clock.GetUtcNow(), cancellationToken);
    }

    internal static async Task<OutboxSummaryDto> OutboxSummaryRowAsync(IApplicationDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var open = db.OutboxMessages.AsNoTracking().Where(m => m.Status != OutboxStatus.Succeeded);
        var pending = await open.CountAsync(m => m.Status == OutboxStatus.Pending && m.AttemptCount == 0, cancellationToken);
        var retrying = await open.CountAsync(m => m.Status == OutboxStatus.Pending && m.AttemptCount > 0, cancellationToken);
        var dead = await open.CountAsync(m => m.Status == OutboxStatus.Failed, cancellationToken);
        var oldest = await open.MinAsync(m => (DateTimeOffset?)m.CreatedAt, cancellationToken);
        return new OutboxSummaryDto(pending, retrying, dead, oldest, oldest is { } at ? (int)Math.Max(0, (now - at).TotalSeconds) : null);
    }

    /// <summary>
    /// Puts messages back in the queue with a fresh attempt count and no waiting. Only a dead-lettered message, or one that is retrying, qualifies; a message that finished or is not
    /// there is skipped with a reason. The update is one conditional statement per call, so a worker that finishes a message at the same moment is never overwritten.
    /// </summary>
    public async Task<Result<RequeueResultDto>> RequeueAsync(IReadOnlyList<Guid>? ids, CancellationToken cancellationToken)
    {
        var (actor, refusal) = await RequireAsync(Permissions.SystemOps.JobsRetry, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (CheckIds(ids) is { } bad)
        {
            return bad;
        }

        var wanted = ids!.Distinct().ToList();
        var now = clock.GetUtcNow();
        var found = await db.OutboxMessages.AsNoTracking().Where(m => wanted.Contains(m.Id)).Select(m => new { m.Id, m.Status, m.AttemptCount }).ToListAsync(cancellationToken);
        var eligible = found.Where(m => m.Status == OutboxStatus.Failed || (m.Status == OutboxStatus.Pending && m.AttemptCount > 0)).Select(m => m.Id).ToList();
        var requeued = eligible.Count == 0
            ? 0
            : await db.OutboxMessages.Where(m => eligible.Contains(m.Id) && (m.Status == OutboxStatus.Failed || (m.Status == OutboxStatus.Pending && m.AttemptCount > 0)))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(m => m.Status, OutboxStatus.Pending)
                    .SetProperty(m => m.AttemptCount, 0)
                    .SetProperty(m => m.NextAttemptAt, now)
                    .SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.UpdatedAt, now)
                    .SetProperty(m => m.UpdatedBy, actor!.Id), cancellationToken);

        var foundIds = found.Select(f => f.Id).ToHashSet();
        var eligibleSet = eligible.ToHashSet();
        var skipped = wanted.Where(id => !eligibleSet.Contains(id))
            .Select(id => new SkippedItemDto(id, foundIds.Contains(id) ? "Not waiting or failed (already delivered)." : "Not found."))
            .ToList();

        await AuditAsync(OutboxAuditEntity, wanted.Count == 1 ? wanted[0].ToString() : "bulk", "requeue", wanted.Count, requeued, eligible, cancellationToken);
        return new RequeueResultDto(wanted.Count, requeued, skipped);
    }

    // ---- Failed notification sends -------------------------------------------------------------------------

    internal static string MaskRecipient(string recipient)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return string.Empty;
        }

        var at = recipient.IndexOf('@', StringComparison.Ordinal);
        if (at > 0)
        {
            return recipient[0] + new string('•', Math.Max(1, at - 1)) + recipient[at..];
        }

        return recipient.Length <= 4 ? new string('•', recipient.Length) : new string('•', recipient.Length - 4) + recipient[^4..];
    }

    private static FailedNotificationDto ToDto(NotificationLog n) =>
        new(n.Id, n.Channel, MaskRecipient(n.Recipient), n.Template, n.Status, n.Attempts, ErrorScrubber.Scrub(n.LastError), n.QueuedAt, n.SentAt);

    public async Task<Result<PagedResult<FailedNotificationDto>>> FailedNotificationsAsync(PageRequest request, FailedNotificationQuery filter, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var query = db.NotificationLogs.AsNoTracking().Where(n => n.Status == NotificationStatus.Failed || n.Status == NotificationStatus.DeadLetter);
        if (filter.Channel is { } channel)
        {
            query = query.Where(n => n.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(filter.Template))
        {
            var template = filter.Template.Trim();
            query = query.Where(n => n.Template == template || n.Template.StartsWith(template));
        }

        if (filter.From is { } from)
        {
            query = query.Where(n => n.QueuedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(n => n.QueuedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(n => n.Template.Contains(term) || (n.LastError != null && n.LastError.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(n => n.SentAt).ThenByDescending(n => n.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<FailedNotificationDto>([.. rows.Select(ToDto)], total, page.Page, page.PageSize);
    }

    /// <summary>
    /// Queues failed sends again through the normal delivery path (<see cref="NotificationService.RequeueAsync"/>): the same outbox message, retry limit, channel switch and
    /// consent decisions as the first send, because the text and its consent check were fixed when the log row was written. Idempotent: a send that is already pending, sent or
    /// skipped is reported as skipped, so a double click queues it once.
    /// </summary>
    public async Task<Result<RequeueResultDto>> RetryNotificationsAsync(IReadOnlyList<Guid>? ids, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.SystemOps.JobsRetry, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (CheckIds(ids) is { } bad)
        {
            return bad;
        }

        var wanted = ids!.Distinct().ToList();
        var logs = await db.NotificationLogs.Where(n => wanted.Contains(n.Id)).ToListAsync(cancellationToken);
        var done = new List<Guid>();
        var skipped = new List<SkippedItemDto>();
        foreach (var id in wanted)
        {
            var log = logs.FirstOrDefault(l => l.Id == id);
            if (log is null)
            {
                skipped.Add(new SkippedItemDto(id, "Not found."));
            }
            else if (await notifications.RequeueAsync(log, cancellationToken))
            {
                done.Add(id);
            }
            else
            {
                skipped.Add(new SkippedItemDto(id, "Not failed (already pending, sent or skipped)."));
            }
        }

        await AuditAsync(NotificationAuditEntity, wanted.Count == 1 ? wanted[0].ToString() : "bulk", "retry", wanted.Count, done.Count, done, cancellationToken);
        return new RequeueResultDto(wanted.Count, done.Count, skipped);
    }

    /// <summary>One message: 404 when it does not exist, 409 <c>SYSTEM.NOT_REQUEUEABLE</c> when it is not waiting or failed.</summary>
    public async Task<Result<RequeueResultDto>> RequeueOneAsync(Guid id, CancellationToken cancellationToken) =>
        Single(await RequeueAsync([id], cancellationToken), "This message is not failed or retrying, so there is nothing to requeue.", ErrorCodes.Platform.MessageNotFound, "Queue message not found.");

    /// <summary>One failed send: 404 when it does not exist, 409 <c>SYSTEM.NOT_REQUEUEABLE</c> when it is not failed.</summary>
    public async Task<Result<RequeueResultDto>> RetryNotificationOneAsync(Guid id, CancellationToken cancellationToken) =>
        Single(await RetryNotificationsAsync([id], cancellationToken), "This message did not fail (it is already pending, sent or skipped).", ErrorCodes.Notify.LogNotFound, "Notification not found.");

    private static Result<RequeueResultDto> Single(Result<RequeueResultDto> result, string conflict, string notFoundCode, string notFound)
    {
        if (result.IsFailure || result.Value.Requeued > 0)
        {
            return result;
        }

        return result.Value.Skipped.Any(s => s.Reason == "Not found.") ? Error.NotFound(notFoundCode, notFound) : Error.Conflict(ErrorCodes.Platform.NotRequeueable, conflict);
    }

    // ---- Shared --------------------------------------------------------------------------------------------

    private static Error? CheckIds(IReadOnlyList<Guid>? ids)
    {
        if (ids is null || ids.Count == 0)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["ids"] = ["Send at least one id."] });
        }

        return ids.Distinct().Count() > MaxBulk
            ? new Error(ErrorCodes.Platform.TooMany, $"At most {MaxBulk} items can be handled at once.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["ids"] = [$"At most {MaxBulk} ids."] })
            : null;
    }

    private async Task AuditAsync(string entity, string entityId, string action, int requested, int done, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var detail = JsonSerializer.Serialize(new { action, requested, done, ids }, JsonSerializerOptions.Web);
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, entity, entityId, AuditAction.Updated, null, detail, currentUser.CorrelationId, currentUser.IpAddress, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }
}
