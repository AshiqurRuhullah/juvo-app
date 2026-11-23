# Step 12h slice 1: background jobs and integration health (notes)

Branch `feature/12h-system-backend`. Contract: `docs/system-api.md`. Scope: FR-AF02 (job dashboard, retry) and FR-AF04 (monitoring basics). Back end only. No migration, no new table, no new package.

## 1. What existed

* `JobLogs` (entity `JobLog`): written by the outbox processor (`outbox.deliver`, one row per attempt, reference = message id) and by the scheduled workers (courier status sync, order timeout, points expiry, campaign send, automations, recurring expenses, ticket auto-close, daily summary).
* `OutboxMessages` (entity `OutboxMessage`): `Status` Pending, Succeeded or Failed (Failed = all attempts used, "dead-lettered"), `AttemptCount`, `NextAttemptAt`, `LastError`. `Requeue()` existed on the entity but nothing used it.
* `NotificationLogs`: status Pending, Sent, Failed, DeadLetter, Skipped. `POST /notifications/logs/{id}/resend` (`notifications.log.retry`) already re-queued through `NotificationService.RequeueAsync`.
* Adapters: `IPaymentGateway` (sandbox + six not-configured slots), `ICourierProvider` (sandbox + six slots), `INotificationSender` (sandbox for SMS, e-mail, WhatsApp), `ISmsSender`/`IEmailSender` (sandbox, OTP and system mail), `/health/live` and `/health/ready` (anonymous, database check).
* Not present: maps or geocoding provider, push provider, any record of failed courier bookings or failed webhooks, request timing.

## 2. What this slice built

* `Juvo.Application/Monitoring`: `ErrorScrubber`; `IIntegrationHealthRecorder` (singleton, in memory); `IntegrationStatusRules`; recording decorators `RecordingCourierProvider` (plus a simulator variant so `is ICourierSimulator` keeps working) and `RecordingPaymentGateway`; `RequestMonitor` (ring buffers); `SystemJobsService`; `SystemHealthService`.
* The courier and payment registries are now registered with a factory that wraps every adapter in its decorator; `NotificationDeliverHandler` reports each send outcome per channel.
* `ISandboxAdapter` marker on the three sandbox adapters (mode Sandbox vs Live).
* `IApplicationDbContext` gained `OutboxMessages` and `JobLogs` (already on the EF context).
* API: `SystemOpsEndpoints` (`/system/jobs/...`, `/system/integrations/...`, `/system/status`), `RequestMonitorMiddleware`, one line in `GlobalExceptionHandler` to record 500s.
* Permissions `system.jobs.view|retry`, `system.health.view`; Manager gets both view codes; error codes `SYSTEM.*`.

## 3. Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| H1 | New `system.*` permissions although `platform.job.*` and `platform.health.view` exist. | The brief named `system.*`; codes are never renamed. Old codes are left unused so no role changes meaning. | Remove the old three from the catalogue only with a migration plan for stored roles. |
| H2 | Manager receives `system.jobs.view` and `system.health.view`, not retry. | "Manager gets view". Retry changes the queue, so Super Admin only; grant per role in the UI. | Edit `DefaultRoles.Manager` (affects new databases; existing roles are edited in the role screen). |
| H3 | Health state is in memory (singleton dictionary), no table. | Needs no migration, cheap, and a restart showing "no data yet" is honest. Persisting would need a write per provider call. | Replace `IntegrationHealthRecorder` with a persisting implementation of the same interface (Step 14 if the host runs several servers). |
| H4 | Outcomes are recorded by decorating the registries, not by editing the call sites. | One place, covers every current and future adapter (also the real ones in Step 14). | Wrap in `AddApplication` (`RecordingAdapters.Wrap`). |
| H5 | Failure = exception or `ErrorType.Failure` result, excluding `*.NOT_CONFIGURED`. | Validation, not-found and conflict answers are our or the caller's refusals, not provider faults. Sandbox gateway `VerifyAsync` for an unknown session is a `Failure` and therefore counts; harmless in sandbox. | `IntegrationCall.TrackAsync`. |
| H6 | Down after 3 failures in a row, Degraded for 1 to 2; a success resets. | Simple and explainable. No time window, because call volume is low and irregular. | `IntegrationStatusRules.DownAfter`. |
| H7 | `POST .../test` only for a sandbox courier (quote). Payment sandbox has no ping (verify of an unknown session is an error); SMS, e-mail, WhatsApp would message a person. | Brief: only where a safe call already exists. | Add a `PingAsync` to an adapter interface in Step 14 and extend `SystemHealthService.TestAsync`. |
| H8 | Test endpoint uses `system.health.view`, not retry. | The only supported check is read-only; no side effect. | Change the policy in `SystemOpsEndpoints`. |
| H9 | Requeue is one conditional `ExecuteUpdate`; eligible = dead-lettered or retrying. A fresh Pending message is refused. | Never overwrite a message the worker just finished; a fresh message needs no help. A retrying one may be pushed to "now". | `SystemJobsService.RequeueAsync`. |
| H10 | Notification retry reuses `NotificationService.RequeueAsync`; a log that is not Failed or DeadLetter is skipped (409 for one id). | Same path, same idempotency and consent decisions as the existing resend; a double click cannot queue twice. | n/a |
| H11 | One audit row per call (`{action, requested, done, ids}`), also when nothing changed. | Brief: who, what, count. The attempt itself is worth seeing. | `SystemJobsService.AuditAsync`. |
| H12 | Notification list masks the recipient and omits the text. | `system.jobs.view` is not a customer-data permission (`notifications.log.view` is). | n/a |
| H13 | Outbox view never returns the payload. | Payloads carry customer data. | n/a |
| H14 | Scrubbing is a pattern filter, applied at read time to stored errors too, so old rows are protected as well. | Errors are stored by workers in many places; one read-side filter covers all. | `ErrorScrubber` (tests list the patterns). |
| H15 | Ring buffer filled by middleware placed before the exception handler; SignalR hub path is skipped. | The final status (500) is seen; a long-lived connection would count as one endless slow request. | `RequestMonitorMiddleware`. |

## 4. Limits, stated plainly

* **Per process, reset on restart:** integration health, request timings, slow routes, recent unhandled errors, uptime. They do not see other servers. Uptime alerts, external alerting, log shipping and dashboards are Step 14 with the hosting choice.
* The status page counts only unhandled 500s as errors; handled problem responses (400 to 409) are not errors here.
* `databaseReachable` is one cheap query with a 5 second limit, not the readiness probe.

## 5. Gaps

| Kind | State today | Retry |
| --- | --- | --- |
| Job runs (`JobLogs`) | Recorded by all workers. | No (a run is not retried; the queue entry it handled is). |
| Outbox messages | Recorded, dead-letter visible. | Yes, one or 100. |
| Failed SMS, e-mail, WhatsApp sends | Recorded in `NotificationLogs`. | Yes, one or 100. |
| Failed courier bookings | **Not recorded.** `ShipmentBooking` answers the failure to the staff member and stores nothing. Only the last provider failure per courier is in the integration health (memory). | No (book again from the order). A durable record would need a small table `JobFailures` (migration, own commit). Not added: nothing could be retried from it that the booking screen does not already offer. |
| Failed webhooks (payment, courier) | **Not recorded.** A refused or failed webhook is answered to the provider, which retries; accepted events leave `PaymentEvents` and `ShipmentEvents`. | No. |
| OTP and system mail (`ISmsSender`, `IEmailSender`) | Written to `NotificationLogs` as Sent by the sandbox; their outcomes are not reported to the health. | n/a |
| Maps, geocoding, push | No adapter exists. | n/a |
| Worker heartbeat for a stopped worker | A worker that is down simply stops writing `JobLogs`; the summary shows an old `lastRunAt`. No expected-interval alert. | n/a |
| Unhandled-error hook | Covered by the buffer unit tests; no integration test provokes a real 500. | n/a |

## 6. Tests (run and seen)

* Application: `MonitoringTests` (scrubbing, status rules, recorder, decorators, ring buffer, concurrency); suite total 271 passed.
* Integration, class `SystemOpsTests` (10): job list and filters and summary, outbox view, single requeue and audit, bulk requeue and limits, failed-send retry (masking, idempotency, outbox message, audit), permission matrix, health rows for sandbox and disabled slots with scrubbed failure, delivery reporting to channel health, connectivity check support, status page and template-only routes.
* Also green: Domain 409 (catalogue and default roles), EndpointPolicy, Seeder, SeederChange, UmsAdmin (roles), NotificationFlow, CourierAdmin, CourierFlow, PaymentFlow, both gateway refund classes.

## 7. Traceability suggestions

| ID | Suggested status | Implemented in |
| --- | --- | --- |
| FR-AF02 | Partial: API done (job runs, summary, queue with requeue, failed sends with retry, integration health); no screen; failed bookings and webhooks not recorded | `SystemJobsService`, `SystemHealthService`, `SystemOpsEndpoints`, `docs/system-api.md` |
| FR-AF04 | Partial: status, slow routes, unhandled errors, backlog (per process); no external alerting or uptime monitor (Step 14) | `RequestMonitor`, `RequestMonitorMiddleware`, `GET /system/status` |
| NFR-012 | Add note: retries and requeues audited; error text scrubbed | `SystemJobsService.AuditAsync`, `ErrorScrubber` |
| NFR-020 | Add note: monitoring basics and job visibility exist; alerting is Step 14 | as FR-AF04 |
