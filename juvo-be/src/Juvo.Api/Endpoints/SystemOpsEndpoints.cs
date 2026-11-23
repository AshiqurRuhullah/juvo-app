using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Monitoring;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Background jobs, queues, integration health and monitoring basics (FR-AF02, FR-AF04). Jobs and queues need <c>system.jobs.view</c> (requeue and retry need
/// <c>system.jobs.retry</c>); integration health and the status page need <c>system.health.view</c>. These are platform-wide, so none of it is branch-scoped.
/// </summary>
internal static class SystemOpsEndpoints
{
    public static WebApplication MapSystemOpsEndpoints(this WebApplication app)
    {
        var system = app.MapApiV1().MapGroup("/system");

        var jobs = system.MapGroup("/jobs").WithTags("System jobs");
        jobs.MapGet("", async ([AsParameters] ListParams list, string? job, JobStatus? outcome, DateTimeOffset? from, DateTimeOffset? to, string? reference, SystemJobsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new JobListQuery(job, outcome, from, to, reference), ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<PagedResult<JobRunDto>>()
            .WithSummary("Job runs, newest first. Filters: job (exact name), outcome (Running, Succeeded, Failed), from, to, reference (exact), search (job name, reference or correlation id).");

        jobs.MapGet("/summary", async (SystemJobsService s, CancellationToken ct) => (await s.SummaryAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<IReadOnlyList<JobSummaryDto>>()
            .WithSummary("One row per job: last run, last success, last failure with its (scrubbed) error, and runs and failures in the last 24 hours.");

        jobs.MapGet("/kinds", async (SystemJobsService s, CancellationToken ct) => (await s.KindsAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<IReadOnlyList<FailureKindDto>>()
            .WithSummary("The failure kinds the platform records and which of them can be retried. Kinds with recorded = false have no failure record today.");

        jobs.MapGet("/{id:guid}", async (Guid id, SystemJobsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<JobRunDto>().WithSummary("One job run.");

        jobs.MapGet("/outbox", async ([AsParameters] ListParams list, OutboxState? state, string? type, DateTimeOffset? from, DateTimeOffset? to, SystemJobsService s, CancellationToken ct) =>
                (await s.OutboxAsync(list.ToRequest(), new OutboxListQuery(state, type, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<PagedResult<OutboxMessageDto>>()
            .WithSummary("The queue of messages not yet delivered: pending, retrying and dead-lettered (all attempts used), newest first. Filters: state, type (name or prefix), from, to, search.");

        jobs.MapGet("/outbox/summary", async (SystemJobsService s, CancellationToken ct) => (await s.OutboxSummaryAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<OutboxSummaryDto>().WithSummary("Counts of pending, retrying and dead-lettered queue messages and the age of the oldest one.");

        jobs.MapGet("/outbox/{id:guid}", async (Guid id, SystemJobsService s, CancellationToken ct) => (await s.GetOutboxAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<OutboxMessageDto>().WithSummary("One queue message with its attempts and scrubbed last error. The payload is not shown.");

        jobs.MapPost("/outbox/requeue", async (RequeueRequest r, SystemJobsService s, CancellationToken ct) => (await s.RequeueAsync(r.Ids, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsRetry).Produces<RequeueResultDto>()
            .WithSummary("Put up to 100 dead-lettered or retrying messages back in the queue with a fresh attempt count. Others are listed as skipped with a reason. Audited.");

        jobs.MapPost("/outbox/{id:guid}/requeue", async (Guid id, SystemJobsService s, CancellationToken ct) => (await s.RequeueOneAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsRetry).Produces<RequeueResultDto>()
            .WithSummary("Requeue one message. 404 when it does not exist, 409 SYSTEM.NOT_REQUEUEABLE when it is not failed or retrying. Audited.");

        jobs.MapGet("/notifications/failed", async ([AsParameters] ListParams list, NotificationChannel? channel, string? template, DateTimeOffset? from, DateTimeOffset? to, SystemJobsService s, CancellationToken ct) =>
                (await s.FailedNotificationsAsync(list.ToRequest(), new FailedNotificationQuery(channel, template, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsView).Produces<PagedResult<FailedNotificationDto>>()
            .WithSummary("Failed and dead-lettered SMS, e-mail and WhatsApp sends, newest first. The recipient is masked and the message text is not shown.");

        jobs.MapPost("/notifications/retry", async (RequeueRequest r, SystemJobsService s, CancellationToken ct) => (await s.RetryNotificationsAsync(r.Ids, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsRetry).Produces<RequeueResultDto>()
            .WithSummary("Send up to 100 failed messages again through the normal delivery path. Sends that are not failed are listed as skipped (a repeat does nothing). Audited.");

        jobs.MapPost("/notifications/{id:guid}/retry", async (Guid id, SystemJobsService s, CancellationToken ct) => (await s.RetryNotificationOneAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.JobsRetry).Produces<RequeueResultDto>()
            .WithSummary("Retry one failed send. 404 when it does not exist, 409 SYSTEM.NOT_REQUEUEABLE when it is not failed. Audited.");

        var health = system.MapGroup("").WithTags("System health");
        health.MapGet("/integrations/health", async (SystemHealthService s, CancellationToken ct) => (await s.IntegrationsAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.HealthView).Produces<IReadOnlyList<IntegrationHealthDto>>()
            .WithSummary("One row per integration slot (payment gateways, couriers, SMS, e-mail, WhatsApp): enabled, mode, last success, last failure with scrubbed message, failures in a row and status (Healthy, Degraded, Down, Disabled, Sandbox). Kept in memory; starts empty after a restart.");

        health.MapPost("/integrations/{key}/test", async (string key, SystemHealthService s, CancellationToken ct) => (await s.TestAsync(key, ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.HealthView).Produces<IntegrationTestResultDto>()
            .WithSummary("Run a safe connectivity check. Only a sandbox courier has one; every other slot answers 409 SYSTEM.NOT_SUPPORTED. Unknown key is 404.");

        health.MapGet("/status", async (SystemHealthService s, CancellationToken ct) => (await s.StatusAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.SystemOps.HealthView).Produces<SystemStatusDto>()
            .WithSummary("Version, uptime, database reachable, queue backlog and oldest age, failed jobs in 24 hours, slow request count, slowest routes and recent unhandled errors. Request and error figures are per process and reset on restart.");

        return app;
    }
}
