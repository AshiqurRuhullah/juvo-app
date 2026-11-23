# Step 12k slice 2: backups screen and data inventory (notes)

Branch `feature/12k-backup-ui`. Front end only (`juvo-front`); contract `docs/backup-api.md`, back-end notes `docs/step12k-backup-notes.md`. FR-AF03.

## 1. Built

* `/admin/system/backups` (System group, menu "Backups", permission `system.backup.view`, Super Admin only by default). Status panel, honest notes, "Back up now" (needs `system.backup.run`), runs list (ten per page, cards on phones).
* "What the system holds" card on `/admin/system/health` (`GET /system/data-inventory`, `system.health.view`).
* Files: `src/modules/system/{backup-logic.ts, backup-api.ts, backup-status.tsx, backup-runs.tsx, backups-view.tsx, inventory-card.tsx}`, page `src/app/(admin)/admin/system/backups/page.tsx`, messages `src/messages/backups.{en,bn}.json` (fragment `backups`), error texts `SYSTEM_BACKUP_RUNNING|NOT_FOUND`, nav key `systemBackups`.
* Shared files touched (additive): `src/lib/nav.ts`, `src/messages/{load.ts,en.json,bn.json}`, `src/modules/system/health-view.tsx` (one component added), `playwright.config.ts` (testMatch, `Backup__Folder` for the e2e API, `E2E.backupFolder`), `.gitignore` (the root rule `backups/` also swallowed the new page folder; one negation added), generated `openapi/v1.json` and `src/lib/api/schema.d.ts`.

## 2. Decisions (reason, how to change)

| # | Decision | Reason | Change |
| --- | --- | --- | --- |
| U1 | Data inventory sits on the health page, not the backups page. | It needs `system.health.view` (the Manager has it) while backups are Super Admin only; one card in one place with one permission. | Move `InventoryCard`. |
| U2 | States: Never (no verified backup), OK (at most 26 h, automatic on), Old (more than 26 h, automatic on), plus a fourth neutral "Manual only" when `Backup.Enabled` is off and a backup exists. | With automatic backups off there is no schedule to be late for; green would mislead and red "Old" would nag. The page also says plainly "Automatic backups are off" with a link to Settings (only if the staff member may open Settings). | `backup-logic.ts` `backupState`, `OLD_AFTER_HOURS`. |
| U3 | Age comes from the API (`lastVerifiedAgeHours`); computed from the time only if absent. | One source of truth. | `ageHours`. |
| U4 | Polling: status and list every 15 s while a run is in progress, every 60 s otherwise, none while the tab is hidden (TanStack `refetchInterval` function, `refetchIntervalInBackground` off). Starting a run invalidates both queries at once. | Brief. | `RUNNING_REFRESH_MS`, `IDLE_REFRESH_MS`. |
| U5 | A 409 (`SYSTEM.BACKUP_RUNNING`) is shown as a calm warning "A backup is already running", not an error. Other failures show the translated API error. | Someone else or the night may have started one. | `startFailure`. |
| U6 | "Triggered by" shows "Staff member" or "Nightly schedule", not a name. | The run row carries only the user id; no staff lookup is allowed for a Super Admin-only screen without another permission. See gaps. | `backup-runs.tsx`. |
| U7 | Next scheduled time is formatted with `timeZone: "Asia/Dhaka"` explicitly. | The API's schedule is Dhaka time whatever the browser zone. | `backup-status.tsx`. |
| U8 | The page has no download, no restore, no delete. | The API has none, on purpose. | n/a |

## 3. Gaps and not built

* No staff name for "triggered by" (API gives only `triggeredByUserId`).
* No alert if a backup is stale beyond the page itself (Step 14 monitoring).
* Reading the folder content or the free space of a network path is not possible (API sends null; the page says so).
* Bengali is a first pass and needs native review (Step 12 note 3).
* A run closed as Failed by the "application stopped" recovery shows its server text as the API gives it (English).

## 4. Tests run and seen

* `npx tsc --noEmit` clean; `npx eslint` on the changed and new files clean (not the whole tree); `npm test`: 36 files, 656 tests passed (new `tests/backup.test.ts`: 15 cases for state with enabled off, age, size and duration formats, polling interval, 409 mapping, inventory order).
* `npm run build` completed (the first run ended at the known "Failed to type check" advisory stop, the second completed).
* Playwright desktop, own ports 5088/3188, database `JuvoDb_E2E_Backup` (dropped before each run): `backup.spec.ts` 11 passed (fresh database shows Never, notes, folder, no download; menu; cancel does nothing; a REAL backup into `test-results/e2e-backup-<db>` becomes Succeeded and verified, the file exists, status turns OK; 409 message; data inventory counts against the API; Manager, view-only, run-only, view+run and Super Admin); `backup-ui-checks.spec.ts` 17 passed (360/768/1280: page, second page, confirm dialog, inventory; dark; Bengali at 360; axe, overflow, 44 px, console errors); `shell`, `permissions`, `system` specs 37 passed, 1 skipped (existing skip).
* The 409 case: a backup of the small e2e database ends in about a second, so the spec starts one through the API from inside a route handler just before the page's own POST (the page's request still reaches the real API; only the timing is arranged). The mapping is also unit tested.
* Not run: tablet, Pixel 7 and iPhone 14 projects, the whole e2e suite, `eslint src e2e tests` over the whole tree.
* The e2e API writes backups into `juvo-front/test-results/e2e-backup-<db>` (git-ignored, removed by the spec); LocalDB runs as the current user so it can write there.

## 5. Traceability suggestions

| ID | Suggested status | Implemented in |
| --- | --- | --- |
| FR-AF03 | Add: screen for status, runs, back up now and the data inventory (front end done). Still not done: off-site copy, encryption, alerts, owner-approved RPO and RTO. | `src/modules/system/backups-view.tsx`, `inventory-card.tsx`, `backup-logic.ts` |
| NFR-010 | Add note: no download or restore in the UI; screen is Super Admin only. | as above |
