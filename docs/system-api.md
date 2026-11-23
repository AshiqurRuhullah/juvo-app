# System API: background jobs, integration health, status (FR-AF02, FR-AF04)

Base `/api/v1/system`. All endpoints need a signed-in staff user and one permission; none is branch-scoped (platform-wide). Lists use the common contract (`page`, `pageSize` max 100, `search`) and answer `{items,total,page,pageSize}`. Enums travel as text. Errors are RFC 9457 ProblemDetails with `code`.

| Permission | Allows | Default roles |
| --- | --- | --- |
| `system.jobs.view` | job runs, summary, queue, failed sends, failure kinds | Super Admin, Manager |
| `system.jobs.retry` | requeue queue messages, retry failed sends | Super Admin |
| `system.health.view` | integration health, connectivity test, status | Super Admin, Manager |

(The older `platform.job.view|retry` and `platform.health.view` codes stay in the catalogue; codes are never renamed. The new endpoints check only the `system.*` codes.)

Every error text shown (`error`, `lastError`, `message`) is scrubbed: first line only, never a stack trace; values after api key, secret, token, password, authorization or signature are replaced by `[hidden]`; bearer tokens, e-mail addresses, phone numbers and long tokens are hidden; GUIDs stay readable; cut to 300 characters.

## Jobs and queues (`/system/jobs`)

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /jobs` | jobs.view | Job runs from `JobLogs`, newest first. Query: `job` (exact name), `outcome` (`Running`, `Succeeded`, `Failed`), `from`, `to` (on start time, `to` exclusive), `reference` (exact), `search` (job name, reference, correlation id). Row: `id, jobName, reference, attempt, outcome, startedAt, finishedAt, durationMs, error, correlationId`. |
| `GET /jobs/{id}` | jobs.view | One run. 404 `SYSTEM.JOB_NOT_FOUND`. |
| `GET /jobs/summary` | jobs.view | One row per job name: `lastRunAt, lastSuccessAt, lastFailureAt, lastError, runs24h, failures24h`. |
| `GET /jobs/kinds` | jobs.view | Failure kinds the platform records: `{kind, recorded, canRetry, note}` for `job-runs`, `outbox`, `notifications`, `courier-bookings`, `webhooks`. The last two have `recorded=false` (see notes, gaps). |
| `GET /jobs/outbox` | jobs.view | Open queue messages (not finished). Query: `state` (`Pending` first attempt waiting, `Retrying` failed before and waiting, `DeadLettered` all attempts used), `type` (name or prefix), `from`, `to` (created), `search` (type, correlation id). Row: `id, type, state, attempts, lastError, nextAttemptAt, createdAt, gaveUpAt, correlationId`. The payload is never returned. |
| `GET /jobs/outbox/summary` | jobs.view | `{pending, retrying, deadLettered, oldestOpenAt, oldestOpenAgeSeconds}`. |
| `GET /jobs/outbox/{id}` | jobs.view | One message. 404 `SYSTEM.MESSAGE_NOT_FOUND`. |
| `POST /jobs/outbox/{id}/requeue` | jobs.retry | Body `{}`. 200 `RequeueResult`; 404 unknown; 409 `SYSTEM.NOT_REQUEUEABLE` when the message is fresh or already delivered. |
| `POST /jobs/outbox/requeue` | jobs.retry | Body `{ids:[guid]}`, 1 to 100 distinct ids (400 `SYSTEM.TOO_MANY` above 100, 400 validation when empty). Always 200 with `RequeueResult`; items not eligible are in `skipped` with a reason. |
| `GET /jobs/notifications/failed` | jobs.view | Sends with status `Failed` or `DeadLetter`, newest first. Query: `channel` (`Sms`, `Email`, `WhatsApp`), `template` (code or prefix), `from`, `to` (queued), `search` (template, error). Row: `id, channel, recipientMasked, template, status, attempts, lastError, queuedAt, lastAttemptAt`. No message text, recipient masked. |
| `POST /jobs/notifications/{id}/retry` | jobs.retry | 200 `RequeueResult`; 404 `NOTIFY.LOG_NOT_FOUND`; 409 `SYSTEM.NOT_REQUEUEABLE` when the send is not failed (already pending, sent or skipped). |
| `POST /jobs/notifications/retry` | jobs.retry | Body `{ids}`, max 100, same as the queue bulk call. |

`RequeueResult` = `{requested, requeued, skipped:[{id, reason}]}`. `requested` counts distinct ids.

Requeue semantics: only a dead-lettered message, or a retrying one, qualifies. One conditional `UPDATE` sets `Status=Pending, AttemptCount=0, NextAttemptAt=now, ProcessedAt=null`; a message the worker finishes at the same moment is not overwritten. The worker then delivers it with the normal retry and backoff. Handlers are idempotent (at-least-once delivery), so a repeat delivery is safe.

Notification retry semantics: the existing path (`NotificationService.RequeueAsync`): log row back to `Pending`, attempts 0, one new `notification.deliver` outbox message. Text, consent and channel-enabled decisions were fixed when the row was first written and are not re-run; quiet hours are a campaign-dispatch rule and are not touched. A send already `Pending` is never queued twice.

Audit: each requeue or retry call writes one `AuditLogs` row (`Entity` `OutboxMessage` or `NotificationLog`, `EntityId` the id or `bulk`, action Updated, `NewValue` JSON `{action, requested, done, ids}`) with the user, branch, correlation id and IP.

## Integration health (`/system/integrations`)

`GET /integrations/health` (health.view) returns one row per slot: payment gateways (`payment:<CODE>`), couriers (`courier:<CODE>`), `sms`, `email`, `whatsapp`. Row: `key, kind, name, enabled, mode (Sandbox|Live), status, lastSuccessAt, lastFailureAt, lastError, consecutiveFailures, canTest`.

Status rule: not enabled -> `Disabled`; enabled sandbox adapter -> `Sandbox`; live adapter: 0 failures in a row `Healthy`, 1 to 2 `Degraded`, 3 or more `Down`. A success resets the count. A slot without keys is `Disabled`; WhatsApp is `Disabled` until its channel setting is on. No maps, geocoding or push adapter exists, so there is no row for them.

`POST /integrations/{key}/test` (health.view): runs a safe check. Today only an enabled sandbox courier offers one (a price quote, no read or write, no message). Every other slot answers 409 `SYSTEM.NOT_SUPPORTED`; unknown key 404 `SYSTEM.UNKNOWN_INTEGRATION`. Result `{key, ok, message, durationMs, at}`; the call is recorded in the health like any other.

What counts as a failure: an exception, or a `Failure`-type result from the provider (not our own validation, not found or conflict answers, not `*.NOT_CONFIGURED`, not a bad webhook signature). Outcomes are recorded for quote, book, cancel, status poll and label of couriers, initiate, verify and refund of gateways, and for each notification delivery per channel.

State is in memory and per process: it starts empty after a restart.

## Status (`GET /system/status`, health.view)

`{version, startedAt, uptimeSeconds, databaseReachable, outbox:{pending,retrying,deadLettered,oldestOpenAt,oldestOpenAgeSeconds}, failedJobs24h, slowRequests, slowThresholdMs, slowestRoutes:[{method,route,count,maxMs,averageMs}], recentErrors:[{correlationId,route,exceptionType,message,at}], requestsSeen, requestsKept, scope}`.

`slowRequests`, `slowestRoutes`, `recentErrors` and `requestsSeen` come from an in-memory ring buffer (last 500 requests, last 100 unhandled errors; slow = 1000 ms or more) filled by a middleware. It stores method, route template (for example `/api/v1/orders/{id:guid}`), status and duration only: no real path, query string, headers or user. Errors are the unhandled 500s from the global exception handler, with correlation id, route, exception type and a scrubbed message (no stack). Per process: it resets on restart and does not see other servers. If the database is unreachable `databaseReachable=false` and the queue and job figures are zero.

Configuration (section `Monitoring`): `SlowRequestMilliseconds` (1000), `RequestBufferSize` (500), `ErrorBufferSize` (100).

## Error codes

`SYSTEM.NOT_SUPPORTED` (409), `SYSTEM.UNKNOWN_INTEGRATION` (404), `SYSTEM.JOB_NOT_FOUND` (404), `SYSTEM.MESSAGE_NOT_FOUND` (404), `SYSTEM.NOT_REQUEUEABLE` (409), `SYSTEM.TOO_MANY` (400); plus the common 400, 401, 403.
