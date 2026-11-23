# Step 12k slice 1: backup, restore and data export tooling (notes)

Branch `feature/12k-backup`. Contract and runbook: `docs/backup-api.md`. Scope: FR-AF03 and the backup part of NFR-004 (capacity and recoverability are the owner's call, see section 6). Back end and `scripts/` only. One migration (`BackupRuns`, own commit), no new package.

## 1. What existed

* `JobLogs` and the worker pattern (outbox, order timeout, recurring expenses ...): a hosted `BackgroundService` with an options class, a disable switch for tests, and a `JobLog` row per run.
* The settings registry (`SettingRegistry`, `PlatformSettings` rows, 10-second cache emptied by the save interceptor).
* `system.*` permissions and the Super-Admin-only pattern (`integrations.*` denied to Manager).
* Database access: `(localdb)\MSSQLLocalDB` in development, SQL Server compatibility level 150, `sqlcmd -E -C` in `scripts/setup-db.ps1`. The machine has no Python and no Docker.
* Not present: any backup, restore or backup record.

## 2. What this slice built

* Domain `BackupRun` (+ `BackupStatus`, `BackupTrigger`), table `BackupRuns`: started, finished, file name, size, compressed, **verified**, scrubbed error, trigger (user or schedule), triggering user id, `FileDeletedAt`.
* `Juvo.Application/Backup`: `BackupNaming` (file name pattern), `BackupRetention` (which files may be deleted), `BackupSchedule` (Dhaka time, due rule), `BackupRunner` (create, execute, recover interrupted runs, retention), `BackupCoordinator` (one-run slot and background start), `BackupService` (staff API), `DataInventoryService`, `IBackupEngine`.
* `Juvo.Infrastructure/Backup`: `SqlServerBackupEngine` (BACKUP with CHECKSUM and INIT, COMPRESSION where allowed, RESTORE VERIFYONLY, folder listing and delete, free space, uploads size) and `BackupWorker` (nightly).
* API `BackupEndpoints`: `GET/POST /system/backups`, `GET /system/backups/{id}`, `GET /system/backups/status`, `GET /system/data-inventory`.
* Permissions `system.backup.view|run` (Super Admin only; `system.backup.*` added to the Manager deny list), error codes `SYSTEM.BACKUP_RUNNING` and `SYSTEM.BACKUP_NOT_FOUND`, setting `Backup.Enabled` (default off), appsettings section `Backup`, `.gitignore` entries.
* `scripts/backup-db.ps1`, `restore-db.ps1`, `restore-test.ps1`, `backup-files.ps1`.

## 3. Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| K1 | The engine uses its own connection and `sp_executesql` with `QUOTENAME(DB_NAME())` and the path as a parameter. | BACKUP cannot run in a transaction; no SQL is built from user text, and the file name is generated, never typed. | `SqlServerBackupEngine.RunBackupAsync`. |
| K2 | Compression: tried when `EngineEdition` is not 4 (Express and LocalDB), and on SQL error 1844 the backup is repeated without it. The run records `compressed`. | LocalDB cannot compress; a real Standard or Enterprise server can. | `SupportsCompressionAsync`. |
| K3 | Verify is part of the run: a run is Succeeded only when RESTORE VERIFYONLY with CHECKSUM passed. A written but unverified file stays on disk, the run is Failed with `verified=false`. | An unverified backup is not a backup; the operator may want to look at the file. | `BackupRunner.ExecuteAsync`. |
| K4 | One run at a time per process (singleton slot); the slot frees a moment after the run row is final (retention runs in between). A run left Running by a stopped process is closed as Failed at the next start. | One application server (CLAUDE.md known issue 2). A second server would need a database lock. | `BackupCoordinator`, `RecoverInterruptedAsync`. |
| K5 | Manual run answers 202 and works in the background (`Task.Run` with its own scope, not cancelled by the request). | A big database takes minutes; HTTP must not wait. An application stop in the middle leaves a Running row that the next start closes. | `BackupCoordinator.StartInBackground`. |
| K6 | The nightly worker polls every minute and decides from data (the last scheduled run's start time versus the last due time), not from a timer. Catch-up window 6 hours; one attempt per night even after a failure. | Survives restarts, no double run, no surprise backup when the setting is switched on in the afternoon. A failed night is visible and retried the next night, not every minute. | `BackupSchedule.IsDue`, `CatchUpHours`. |
| K7 | Retention matches the exact name pattern for this database, by the time stamp in the name (not file time), top level only, skips reparse points, and never deletes the newest verified file. Files made by the scripts use the prefix `juvo-manual_` and are never deleted automatically. | "Only files this service created"; a shared folder or a hand-copied file must be safe. | `BackupRetention`, `SqlServerBackupEngine.DeleteFile` (checks the name again). |
| K8 | `Backup.Enabled` is the only runtime setting (registry, group Operations, `settings.system.edit`, default off). Folder, retention, time and the worker switch are appsettings. | The brief: folder read-only from appsettings. A staff member must not be able to point the backup at a path. | `BackupOptions`, `SettingRegistry`. |
| K9 | `system.backup.view|run` are new codes, Super Admin only, and `system.backup.*` is in the Manager deny list. Viewing needs `view`; `run` alone does not allow viewing (same split as jobs). | High-risk data (a backup holds everything). | `DefaultRoles`, `PermissionCatalog`. |
| K10 | Status exposes the absolute backup folder path to those who can view backups. | Super Admin needs to know where the files are; the folder is not served over HTTP. | Remove `Folder` from `BackupStatusDto`. |
| K11 | No in-app restore, no download of a backup. | Too dangerous and too much personal data for a web API; an operator script with a typed confirmation is the safer tool. | n/a (deliberate). |
| K12 | Restore script restores into a new database by default, verifies the file first, moves every file next to the instance's default data and log folders named after the new database, prints the exact statement, refuses to overwrite unless `-ReplaceExisting` and a typed (or `-ConfirmName`) database name, and uses SINGLE_USER with rollback only after that. | Never overwrite by accident; the original files are never touched. | `scripts/restore-db.ps1`. |
| K13 | Restore test = restore, `DBCC CHECKDB`, key tables exist (counts printed, not judged), last `__EFMigrationsHistory` row equals the newest `*.Designer.cs` migration in the code, newest 50 orders: `GrandTotal = Subtotal - PromotionDiscount - CouponDiscount - ManualDiscount + DeliveryCharge`. | The total is fixed by the order domain at placement (`Order.cs`), so a mismatch means damage, not an edit. With no orders the check is SKIP, shown plainly. A restore older than the code reports FAIL on the migration line on purpose (apply migrations before use). | `scripts/restore-test.ps1`. |
| K14 | Data inventory lives on `system.health.view` (Manager has it) because it is counts only; it counts with all query filters ignored (archived rows, every branch). | The brief; counts hold no personal data. | `DataInventoryService`. |
| K15 | Uploads size counts at most 200,000 files and says when it stopped. | "If cheap": a walk over millions of files must not run on a status call. | `MaxUploadFilesCounted`. |
| K16 | Scripts are plain PowerShell (5.1 and 7), `sqlcmd -E -C`, quote all paths, no secrets. | The machine has no Python; Windows is the default environment. | n/a |

## 4. Honest gaps

* **Same-disk backups are not disaster recovery.** Nothing here copies a backup off the machine. An off-site copy (second machine, cloud storage) with its own access control is an operator task now and a Step 14 choice. `docs/backup-api.md` gives the `robocopy` example only.
* **No encryption at rest** of the `.bak` files. SQL Server offers backup encryption and TDE, both need certificates or keys that must themselves be backed up and kept apart: a Step 14 decision. Until then the folder needs strict Windows permissions and the disk should use BitLocker.
* **Uploaded files** (`/files`) are not in a database backup: `scripts/backup-files.ps1` zips them; it must be scheduled by the operator and copied off-site too. A zip taken while uploads happen can miss a new file (the script detects a changed file count and says so).
* The nightly backup and the retention run in the API process and the folder must be writable by the **SQL Server service account**, which is a different account from the API's on a real server. A wrong folder shows as a Failed run with the SQL Server error text (scrubbed).
* The backup status does not alarm anyone by itself: a stale backup shows as `lastVerifiedAgeHours`, and a failed night shows in the jobs screen and the failed-jobs count. E-mail or SMS alerts are Step 14 monitoring.
* One server only (in-memory run slot, per-process worker).
* A manual run frees its slot a moment after the row turns Succeeded (retention runs in between); a second click in that window gets 409.
* No screen: the API is ready for a screen (front-end slice not built).
* **Owner decisions (not mine):** RPO (how much recent data may be lost: nightly backup = up to a day; shorter needs hourly log backups or a replica, which this slice does not do, the database is in the default recovery model and no log backups are taken), RTO (how long a restore may take; measure it with `restore-test.ps1` on real data size), where the off-site copy goes, encryption, retention beyond 14 days. NFR-001..003 (availability, API speed, front-end speed) are not about backups.

## 5. Tests (run and seen)

* Application, `BackupRulesTests`: 24 cases (name pattern and unparsable names, retention picks only own old files of this database, newest verified kept, retention under one day, Dhaka due time and next time, time format, due rule once per night and the catch-up edges, error scrubbing). Whole project 383 passed.
* Domain: 420 passed (permission catalogue and default roles).
* Integration, class `BackupTests` (8, run three times green, about 25 s each): manual backup written, verified and listed, file exists, audit row, status; 409 while a run is held (a gate in the test engine) and the slot frees afterwards; a stale Running row closed as Failed; permission matrix (anonymous 401, no permission, Manager, run-only, view-only, health-only, data inventory) and no stored role rows for Manager and the other default roles; data inventory counts and no personal data; retention against real files (old own file removed, the kept, fresh, other-database, foreign-name, `.txt` and sub-folder files untouched, `FileDeletedAt` set, engine refuses foreign names and paths); nightly worker (off by default, then on and due: one Schedule run, one JobLog row, second call does nothing); a folder that cannot be created fails cleanly. These use the real SQL Server engine against the throw-away LocalDB test database and a temp folder (LocalDB runs as the current user, so it could write there).
* Also green (filtered classes): `SettingsCentreTests` 11, `EndpointPolicyTests` 7, `SeederTests` 4, `SeederChangeTests` 3, `SystemOpsTests` 10, `UmsAdminTests` 16. Build `dotnet build --no-incremental`: 0 warnings, 0 errors. The whole integration suite was not run (over an hour on this machine).
* The `Backup.Enabled` default equals the registry default (the settings test that keeps registry and module defaults in step passed).

## 6. Real script runs (this machine, LocalDB 2019, throw-away databases only)

Throw-away database `JuvoDb_ScriptTest_<timestamp>` created from the real migrations (`dotnet ef database update --connection`, 121 tables, last migration `20261123125459_BackupRuns`) with a few inserted rows (1 branch, 2 customers, 1 product, 1 variant, 1 stock level, 2 orders, 2 order items, 3 audit rows, 1 payment, 1 invoice). The dev `JuvoDb` was never touched. Every throw-away database and file was dropped or deleted afterwards.

* `backup-db.ps1`: `Options: CHECKSUM, INIT (compression not used: Express/LocalDB edition)`, `Size: 8,413,696 bytes`, `Verified: yes`, exit 0. Same result under Windows PowerShell 5.1.
* `restore-db.ps1`: `-WhatIfOnly` printed the plan and changed nothing (exit 0); restore into `<db>_copy` printed the MOVE of both files and `RESTORED: ... is ONLINE with 121 tables` (exit 0), the copy held Orders 2, Customers 2, AuditLogs 3; second run onto the same name: `REFUSED: database ... already exists` (exit 3); `-ReplaceExisting` with a wrong `-ConfirmName`: `REFUSED: the confirmation did not match` (exit 5); with the right name: restored over it (exit 0); with no `-Database`: new name `<db>_Restored_<timestamp>` (exit 0).
* `restore-test.ps1` on the good backup: `PASS` for Restore, DBCC CHECKDB, Key tables (counts printed), Migration history (`20261123125459_BackupRuns equals the code`), Order totals (2 newest orders match); `RESULT: PASS`, exit 0, throw-away database dropped. After making one order's `GrandTotal` wrong and taking a new backup: `[FAIL] Order totals: 1 of 2 newest orders have a GrandTotal that does not match the formula`, `RESULT: FAIL (1 check(s) failed)`, exit 1, throw-away dropped. Also run under Windows PowerShell 5.1 (same report).
* `backup-files.ps1` on a temp uploads tree (3 files in nested folders): `Size: 428 bytes, 3 entries`, `Verified: the zip opens and holds every file`, exit 0 (same under 5.1); a missing uploads folder prints "Nothing to back up" and exits 0.
* Not run for real: `-Server` against a non-LocalDB instance, `COMPRESSION` (no Standard or Developer edition on this machine), a very large database, the Windows Task Scheduler commands in the runbook.

## 7. Traceability suggestions

| ID | Suggested status | Implemented in |
| --- | --- | --- |
| FR-AF03 | Partial: backup (manual and nightly, verified, retention, audit), operator restore and restore-test scripts, uploads zip, data inventory, runbook; tested for real on throw-away databases. Not done: off-site copy, encryption, a screen, alerts, a restore test on production data and an owner-approved RPO and RTO. | `BackupService`, `BackupRunner`, `SqlServerBackupEngine`, `BackupWorker`, `BackupEndpoints`, `scripts/*.ps1`, `docs/backup-api.md` |
| NFR-004 | Add note: recoverability tooling exists (backup, restore test); capacity targets are still untested (Step 13). | as FR-AF03 |
| NFR-010 | Add note: backups hold personal data: no HTTP download, Super Admin only, file names carry none; unencrypted at rest (Step 14). | `BackupEndpoints`, `DefaultRoles` |
| NFR-012 | Add note: manual backup runs are audited; nightly failures reach the job log. | `BackupService.StartAsync`, `BackupWorker` |
| FR-AF02 | Add note: the nightly backup is a job with a `JobLogs` row (`system.backup`). | `BackupWorker` |
| FR-AF04 | Add note: status shows the age of the last verified backup and free space. | `GET /system/backups/status` |
| FR-B09, FR-P (exports) | Unchanged: data exports reuse catalog import/export, report exports and the privacy export (see the table in `docs/backup-api.md`). | existing |
