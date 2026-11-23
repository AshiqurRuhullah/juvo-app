# Step 12h slice 2: background jobs and integration health screens (notes)

Branch `feature/12h-system-ui`. Front end only (`juvo-front`); the back end is slice 1 (`docs/system-api.md`, `docs/step12h-system-notes.md`). Scope: FR-AF02 (job dashboard, retry) and FR-AF04 (monitoring basics). No new package, no `juvo-be` change.

## 1. Built

| Route | Permission | What |
| --- | --- | --- |
| `/admin/system/jobs` | `system.jobs.view` | Summary cards per job (last run, last success, runs and failures in 24 h, last scrubbed error), a note listing the failure kinds the platform does not record (`GET /jobs/kinds`), three tabs. |
| `/admin/system/health` | `system.health.view` | Integration cards grouped as payment gateways, couriers and messages; system status panel; Refresh button and a 30 s auto refresh. |

Menu: the existing **System** group now holds Settings, Background jobs and System health (so System is a parent row, not a single link). `src/lib/nav.ts` items `system-jobs` and `system-health`, icons Activity and HeartPulse, scope "anywhere" (the screens are platform-wide). Specs that clicked the old single Settings link (`shell.spec`, `settings.spec`) now open the System parent first.

Jobs tabs (retry buttons, checkboxes and the bulk bar exist only with `system.jobs.retry`):

* **Job runs**: paged list (10 rows, shared pager, cards on phones), filters job (options from the summary), outcome, date range, exact reference, search; detail dialog with attempt, started, finished, duration, correlation id and the scrubbed error.
* **Queue**: state chips Pending, Retrying, Dead-lettered with counts from `/jobs/outbox/summary`, type filter (name or prefix), date range, search. Requeue one (confirm), or tick rows (kept across pages, at most 100) and Requeue selected (confirm that says what happens). A bulk answer is shown in full: how many were requeued, and each skipped id with its reason. A fresh Pending message has no button and no checkbox ("Waiting for its first try"). The payload is never shown.
* **Failed sends**: SMS, e-mail, WhatsApp; masked recipient, channel, template, scrubbed error, attempts; filters channel, template prefix, date range, search; Retry one and selected with the same result display.

Health: each card shows a status badge (colour, icon and text), mode (Sandbox or Live), last success, last failure, scrubbed last error, failures in a row. "Test connection" appears only where the API says `canTest` (the enabled sandbox courier); every other card says plainly that a test is not available (a 409 `SYSTEM.NOT_SUPPORTED` from a race would show the translated error toast). The test result (ok or failed, duration, message) shows in the card, and the card refreshes because the call is recorded. The status panel shows database reachable, version, uptime and start time, queue backlog (pending, retrying, dead-lettered, oldest age), failed jobs in 24 h, slow request count with threshold, requests seen and kept, the slow routes table (route templates only) and recent unhandled errors with correlation id. A permanent note says the figures are per server process and reset on restart.

Code: `src/modules/system/{logic.ts, api.ts, common.tsx, use-selection.ts, bulk-bar.tsx, runs-panel.tsx, outbox-panel.tsx, failed-panel.tsx, jobs-view.tsx, health-view.tsx, status-panel.tsx}`; pages under `src/app/(admin)/admin/system/`; texts `src/messages/sysops.{en,bn}.json` (namespace `sysops`, registered in `load.ts`; `errors.SYSTEM_*` added to `en.json` and `bn.json`; module label `ums.permissions.modules.system`); admin scope is "all" so `scopes.ts` needed no change.

## 2. Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| U1 | System menu group holds the new items next to Settings. | The group already exists and "System" is where staff look for jobs and health. | `ADMIN_NAV` in `src/lib/nav.ts`. |
| U2 | A live slot the API reports as Healthy but with no recorded success or failure is shown as "No calls yet" (neutral grey), never green. | Brief: never green for something not recorded. The API derives Healthy from "0 failures in a row", which is also true before any call. | `shownStatus` in `logic.ts` (unit tested). |
| U3 | Sandbox status is the info colour, not green. | Sandbox is not production health. | `statusLook` in `logic.ts`. |
| U4 | Checkboxes only on rows that can be requeued (Dead-lettered, Retrying). Failed sends: all rows. | The API refuses a fresh Pending message (409) and skips it in bulk; offering it only to show a refusal is noise. The server still skips and explains if state changed between ticking and confirming. | `canRequeue` in `logic.ts`. |
| U5 | Selection survives paging, limited to 100 on the client, with a plain message at the limit; remaining boxes are disabled. | API limit is 100 distinct ids (`SYSTEM.TOO_MANY`). | `MAX_BULK`, `toggleId`, `togglePage`. |
| U6 | Single requeue or retry shows a toast; bulk shows the full result panel. | The single call answers 409 for an ineligible item, which is handled as an error toast; a bulk call is always 200 with skipped reasons. | `run` in the two panels. |
| U7 | Skipped reasons: the three English sentences of the API are mapped to translated texts; any other text is shown as sent. | Reasons are free text in the contract. | `REASONS` in `logic.ts`. |
| U8 | Auto refresh uses TanStack Query `refetchInterval: 30000` with `refetchIntervalInBackground: false`. | Pauses while the tab is hidden without own timers. Only the health page refreshes by itself; the jobs page has a Refresh button. | `REFRESH_MS` in `logic.ts`, hooks in `api.ts`. |
| U9 | Job detail comes from the list row; `GET /jobs/{id}` and `/jobs/outbox/{id}` are not called. | The row has every field. | n/a |
| U10 | Slow routes table title and empty state say "slow" (the buffer only keeps requests of 1000 ms or more). | The contract calls it `slowestRoutes` but it holds only slow requests; "no requests recorded" would mislead. | `sysops.status2.routes.*`. |
| U11 | Error texts are shown exactly as the API scrubbed them, in a monospace span. | Scrubbing is the API's job (H14); the screen adds none and removes none. | `ErrorText` in `common.tsx`. |

## 3. Contract gaps and observations

* Enum `JobStatus`, `OutboxState`, `IntegrationStatus` arrive as text; counts arrive as number or string (the generated types say both): every count goes through `toNumber`.
* The generated client types the single requeue as having no body; the docs say `{}`. The screen sends no body and the API accepted it.
* The scrubber matches `api_key`, `apikey`, `api-key` but not "api key" with a space; the doc says "api key". Seed texts in the specs use `api_key=`.
* `slowestRoutes` holds only slow requests, so on a healthy server the table is empty (shown honestly).
* Failed courier bookings and webhooks are not recorded by the platform; the jobs page says so (from `/jobs/kinds`).
* No maps, geocoding or push adapter, so no cards for them.
* State is per process: the page says so twice (memory note on the cards, scope note on the status panel).

## 4. Not built

Alerts or e-mail on a failed job, charts or history of health over time (state is in memory), job pause or manual run, a durable failure record for courier bookings and webhooks, external uptime monitoring (Step 14).

## 5. Tests

See the end of this file for the numbers actually run.

## 6. Traceability suggestions

| ID | Suggested status | Implemented in |
| --- | --- | --- |
| FR-AF02 | Done for the shipped scope: job summary, runs, queue requeue (one or 100), failed sends retry, integration health with a sandbox connectivity test; failed bookings and webhooks are not recorded (documented gap) | `src/modules/system`, `/admin/system/jobs`, `/admin/system/health` |
| FR-AF04 | Partial: status, slow routes, unhandled errors, backlog on screen (per process); no external alerting or uptime monitor (Step 14) | `status-panel.tsx` |
| NFR-020 | Add note: job visibility and health screens exist; alerting is Step 14 | as FR-AF04 |

## 7. Test numbers (run and seen)

* `npx tsc --noEmit`: clean. `eslint` on `src/modules/system`, the two pages, `nav.ts`, the new specs and support file: clean. `npm test` (vitest): 33 files, 591 tests passed (19 new in `tests/system.test.ts`; message key and placeholder parity and scope tests included).
* `npm run build`: completes (needed a re-run after the known advisory notice each time).
* Playwright desktop, own ports and database: `system.spec.ts` 21 tests passed (1.5 min). `system-ui-checks.spec.ts` 23 tests (jobs, queue, failed sends, health at 360/768/1280, view-only, dark, Bengali; axe, overflow, 44 px, console errors): first run 21 passed and 2 failed (dark and Bengali, a timeout and a missing card while other builders loaded the machine), second run 1 failed (failed sends at 360 px), both passed alone, third full run 23 passed. Treat the spec as slightly load sensitive.
* Menu specs re-run after the System group change: `shell.spec`, `permissions.spec` and `settings.spec` menu cases, 13 passed. Other specs were not re-run. Tablet, Android and iPhone projects were not run (the new specs are desktop projects with explicit viewports).
* Screenshots kept: `juvo-front/docs/ui-checks/system/` (runs, queue, health at key widths, dark, Bengali).
