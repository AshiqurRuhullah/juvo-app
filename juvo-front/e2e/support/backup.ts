import { existsSync, readdirSync, rmSync } from "node:fs";
import { E2E } from "../../playwright.config";
import { sql as rawSql } from "./env";
import { call } from "./cod";
import { adminAccess } from "./returns";

/**
 * Helpers of the backup specs. Real backups are taken by the real API into `E2E.backupFolder` (a git-ignored folder under
 * test-results, set for the e2e API in playwright.config.ts); LocalDB runs as the current Windows user so it can write there.
 * The UI-check spec also writes a few history rows straight into the throw-away database (a real run cannot be made to fail on demand).
 */
export type BackupStatusApi = { enabled: boolean; folder: string; running: boolean; lastVerifiedAt: string | null; lastVerifiedFileName: string | null; lastRun: { status: string } | null };
export type InventoryApi = { at: string; tables: { key: string; rows: number | string }[]; uploadsBytes: number | string | null; uploadsFiles: number | string | null };

export async function backupStatus(): Promise<BackupStatusApi> {
  return (await call<BackupStatusApi>("GET", "/api/v1/system/backups/status", { token: await adminAccess(), expect: 200 })).json;
}

export async function dataInventory(): Promise<InventoryApi> {
  return (await call<InventoryApi>("GET", "/api/v1/system/data-inventory", { token: await adminAccess(), expect: 200 })).json;
}

/** Starts a real backup through the API (what the button does); returns the HTTP status (202, or 409 when one is running). */
export async function startBackupViaApi(): Promise<number> {
  return (await call("POST", "/api/v1/system/backups", { token: await adminAccess(), body: {} })).status;
}

/** Waits until no backup is running any more (polls the status). */
export async function waitUntilIdle(timeoutMs = 90_000): Promise<void> {
  const until = Date.now() + timeoutMs;
  while (Date.now() < until) {
    if (!(await backupStatus()).running) return;
    await new Promise((r) => setTimeout(r, 1000));
  }
  throw new Error("a backup was still running after " + timeoutMs + " ms");
}

/** Files of the e2e backup folder (empty when it does not exist yet). */
export function backupFiles(): string[] {
  return existsSync(E2E.backupFolder) ? readdirSync(E2E.backupFolder) : [];
}

/** Removes the whole e2e backup folder (its path is inside test-results; refuses any other path). */
export function cleanBackupFolder(): void {
  if (!E2E.backupFolder.includes("e2e-backup-")) throw new Error(`Refusing to delete ${E2E.backupFolder}`);
  rmSync(E2E.backupFolder, { recursive: true, force: true });
}

function sql(query: string): string {
  return rawSql(`SET QUOTED_IDENTIFIER ON; ${query}`);
}

export const newSeedTag = () => Math.random().toString(36).slice(2, 8);

/** History rows for the screenshots: `count` finished runs, one of them failed with an error text; tagged by file name or error text so they can be removed. */
export function seedHistory(tag: string, count = 12): void {
  const rows: string[] = [];
  for (let i = 0; i < count; i += 1) {
    const failed = i === 3;
    const minutesAgo = 60 * 5 * (i + 1);
    const file = failed ? "NULL" : `N'e2e-seed-${tag}-${i}.bak'`;
    rows.push(
      `(NEWID(), ${i % 2 === 0 ? 2 : 1}, ${failed ? 2 : 1}, DATEADD(MINUTE, -${minutesAgo}, SYSDATETIMEOFFSET()), DATEADD(SECOND, -${minutesAgo * 60 - 47}, SYSDATETIMEOFFSET()), ${file}, ${failed ? "NULL" : 8413696 + i * 1000}, 0, ${failed ? 0 : 1}, ${failed ? `N'Cannot open backup device (e2e seed ${tag}). Operating system error 5(Access is denied.)'` : "NULL"}, SYSDATETIMEOFFSET())`,
    );
  }
  sql(
    `INSERT INTO BackupRuns (Id, [Trigger], Status, StartedAt, FinishedAt, FileName, SizeBytes, Compressed, Verified, Error, CreatedAt) VALUES ${rows.join(",")}`,
  );
}

export function cleanHistory(tag: string): void {
  sql(`DELETE FROM BackupRuns WHERE FileName LIKE N'e2e-seed-${tag}-%' OR Error LIKE N'%(e2e seed ${tag})%'`);
}
