# Backup API and operator runbook (FR-AF03)

Base `/api/v1/system`. Staff only, platform-wide (not branch-scoped), **Super Admin by default**. The Manager role is explicitly denied `system.backup.*`. Errors are RFC 9457 ProblemDetails with `code`.

There is **no restore endpoint and no download of a backup file** in the web API, on purpose: restoring replaces the live data and a `.bak` holds every customer's data. Restore is an operator action with `scripts/restore-db.ps1`.

| Permission | Allows | Default roles |
| --- | --- | --- |
| `system.backup.view` | list runs, one run, status | Super Admin |
| `system.backup.run` | start a backup now | Super Admin |
| `system.health.view` (existing) | `GET /data-inventory` | Super Admin, Manager |

## Endpoints

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /backups` | backup.view | Paged (`page`, `pageSize` max 100), newest first. Row: `id, trigger (User, Schedule), triggeredByUserId, status (Running, Succeeded, Failed), startedAt, finishedAt, durationSeconds, fileName, sizeBytes, compressed, verified, error, fileDeletedAt`. `error` is scrubbed (first line, no keys, tokens, e-mail, phone numbers). |
| `POST /backups` | backup.run | Body `{}`. **202** with the run (`status: Running`) and a `Location` header; the backup continues in the background, poll `GET /backups/{id}`. **409 `SYSTEM.BACKUP_RUNNING`** when one is already running (manual or nightly). Audited (`AuditLogs`, entity `BackupRun`, one row per manual run, user, correlation id, IP). |
| `GET /backups/{id}` | backup.view | One run. 404 `SYSTEM.BACKUP_NOT_FOUND`. |
| `GET /backups/status` | backup.view | `{enabled, folder, retentionDays, dailyAt, timeZone, nextScheduledAt, running, lastRun, lastVerifiedAt, lastVerifiedAgeHours, lastVerifiedFileName, folderFreeBytes}`. `enabled` is the `Backup.Enabled` setting. `nextScheduledAt` is null when the setting is off or the nightly worker is switched off in the configuration. `folderFreeBytes` is null for a network path. |
| `GET /data-inventory` | health.view | `{at, tables:[{key, rows}], uploadsBytes, uploadsFiles, uploadsTruncated}`. Keys: `customers, orders, products, variants, stockLevels, invoices, payments, auditLogs`. Counts include archived rows and every branch. **No personal data.** The uploads size counts at most 200,000 files (`uploadsTruncated` says so). |

A run has `status` Succeeded only when the file was written **and** `RESTORE VERIFYONLY ... WITH CHECKSUM` passed on it. A written file that does not verify is a Failed run with the file name kept and `verified: false`; a run left Running by a stopped process is closed as Failed ("The application stopped before this backup finished.") the next time a backup starts.

Error codes: `SYSTEM.BACKUP_RUNNING` (409), `SYSTEM.BACKUP_NOT_FOUND` (404); plus the common 401, 403.

## Configuration

Appsettings section `Backup` (read-only at run time, needs a restart):

| Key | Default | Meaning |
| --- | --- | --- |
| `Folder` | `App_Data/backups` | Absolute folder, or relative to the application folder. **SQL Server writes the file**, so this folder must be writable by the SQL Server service account (LocalDB: the Windows user that runs the API). In production use an absolute path on a different disk or volume than the database files. |
| `RetentionDays` | 14 | The application's own files older than this are deleted after a successful run. The newest verified file is always kept. |
| `DailyAt` | `02:30` | Nightly time, `HH:mm`, Asia/Dhaka (UTC+06:00). |
| `WorkerEnabled` | true | Starts the nightly worker. Tests and migration runs set it to false. |
| `CommandTimeoutMinutes` | 120 | Longest a BACKUP or VERIFY statement may run. |

Runtime setting (Settings centre, group Operations, `settings.system.edit`): **`Backup.Enabled`**, default **off** everywhere (Development and tests included). Turn it on only once the folder and an off-site copy are arranged.

## What the application does

* **Manual** (`POST /backups`): creates a `BackupRuns` row, runs `BACKUP DATABASE ... TO DISK = <folder>\juvo-backup_<database>_<UTC yyyyMMddTHHmmssZ>_<8 hex>.bak WITH CHECKSUM, INIT` (plus `COMPRESSION` on editions that allow it; LocalDB and Express do not, the engine detects it and falls back), then `RESTORE VERIFYONLY ... WITH CHECKSUM`, then the retention clean-up.
* **Nightly**: a hosted worker checks once a minute. When `Backup.Enabled` is on and the Dhaka time has passed (within 6 hours, so a restart at 02:35 still takes the 02:30 backup) without a scheduled run since, it takes one backup, writes a `JobLogs` row (job `system.backup`, reference = run id) so it shows on the jobs screen and counts in the failed jobs of `GET /system/status`, and applies the retention. One attempt per night: a failed run is visible but is not retried until the next night.
* **Retention** deletes only files of the exact name pattern above, for this database, in the top level of the folder, older than `RetentionDays` by the time stamp in the name; never the newest verified file, never a sub-folder, never a link. Files made by `scripts/backup-db.ps1` (`juvo-manual_` prefix) and anything else in the folder are never touched.
* File names contain no personal data (prefix, database name, time, run id fragment).

## Runbook

### Take a backup
* From the application: `POST /api/v1/system/backups` (Super Admin).
* From the machine (no application needed):
  `.\scripts\backup-db.ps1 -Database JuvoDb -Folder "D:\JuvoBackups"` (add `-Server ".\SQLEXPRESS"` for another instance). It prints the file, size and `Verified: yes`, exit code 0 only when verified.
* Uploaded files (product pictures, return photos, ticket attachments) are **not** in the database backup: `.\scripts\backup-files.ps1 -Source "<Storage:RootPath>" -Folder "D:\JuvoBackups" -KeepDays 30` writes `juvo-files-<time>.zip` and checks it.

### Schedule it
* Nightly database backup: switch on the setting `Backup.Enabled` after checking `Backup:Folder`; look at `GET /system/backups/status` next morning (`lastVerifiedAgeHours` should be under 24).
* Nightly files zip (Windows Task Scheduler, run after the database backup):
  `schtasks /Create /SC DAILY /ST 03:15 /TN "Juvo files backup" /TR "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"F:\JUVO App\JuvoApp\scripts\backup-files.ps1\" -Source \"D:\Juvo\uploads\" -Folder \"D:\JuvoBackups\" -KeepDays 30"`
* Weekly restore test (below), also with Task Scheduler.

### Test a restore (do this before launch and every week)
`.\scripts\restore-test.ps1 -Database JuvoDb -Folder "D:\JuvoBackups"`

It takes the newest backup, restores it into the throw-away database `JuvoDb_RestoreTest_<timestamp>`, runs `DBCC CHECKDB`, checks that the key tables exist and prints their row counts, checks that the last row of `__EFMigrationsHistory` equals the newest migration in the code, recomputes `GrandTotal` of the 50 newest orders, prints a PASS or FAIL report, drops the throw-away database and exits 0 or 1. Without orders the order check is reported as SKIP (not a pass). Treat any FAIL as an incident.

### Restore for real (disaster or move)
1. Stop the application (and anything else connected to the database).
2. Keep the damaged database if it still exists (restore into a new name first):
   `.\scripts\restore-db.ps1 -BackupFile "D:\JuvoBackups\juvo-backup_JuvoDb_20261123T203000Z_ab12cd34.bak" -Database JuvoDb_Restored`
   The script verifies the file, prints the plan (every file moved into the instance data folder, named after the new database), restores, and prints `RESTORED`.
3. Check it (for example `restore-test.ps1 -BackupFile <file>`), then point `ConnectionStrings:Default` at `JuvoDb_Restored`, or replace the live database:
   `.\scripts\restore-db.ps1 -BackupFile <file> -Database JuvoDb -ReplaceExisting` (it asks you to type the database name; `-ConfirmName JuvoDb` does the same for automation). It refuses without both.
4. Start the application. If the backup is older than the code, apply the newer migrations: `dotnet run --project juvo-be/src/Juvo.Api -- --migrate` (Production) or just start it in Development.
5. Restore the uploaded files from the newest `juvo-files-*.zip` into `Storage:RootPath` (`Expand-Archive`).
6. Tell the owner what time the backup was taken: everything after it (orders, stock changes) must be re-entered or recovered from the courier, payment and SMS providers.

### Copy off-site (not done by the application)
A backup on the same disk as the database does not survive a disk, machine or site loss. Copy `Backup:Folder` and the files zips to another machine or cloud storage every night (for example `robocopy "D:\JuvoBackups" "\\other-pc\JuvoBackups" /E /XO` as a scheduled task, or the provider's own sync client). Choosing the target, its access control and its encryption is the owner's decision (Step 14).

## Data exports (what each covers)

| Export | Where | Covers | Personal data |
| --- | --- | --- | --- |
| Product import/export | `GET /catalog/products/export`, `import`, `import-template` (`catalog.product.export`, `catalog.product.import`) | Products and variants, prices, sale dates, opening or branch stock; cost only with `catalog.product.view-cost`. CSV or Excel. | No |
| Reports export | `GET /reports/<name>/export` (`reports.<name>.export`), `GET /accounting/export` (`accounting.export.run`) | The Reports screens (sales, orders, inventory, finance, courier, customers) and the accounting books; CSV, Excel or PDF. | Customer reports and orders name customers |
| Privacy export | `POST /privacy/export` (`customers.privacy.export`) | Everything held about one customer as JSON; logged, limited per hour. | Yes, one person |
| Data inventory | `GET /system/data-inventory` (`system.health.view`) | Row counts and uploads size only. | No |
| Database backup | this page | The whole database, once, for restoring. | Yes, everything |

There is no whole-system export and no import other than the catalog import: moving the system means restoring a backup.
