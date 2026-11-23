/**
 * Pure rules of the backups screen and the data inventory (FR-AF03; docs/backup-api.md). No React, no network: the state of the
 * newest verified backup, size and duration formatting, the polling interval and the mapping of a failed start live here so they can
 * be unit tested.
 */
import type { Tone } from "./logic";

/** A verified backup older than this many hours is "Old" when automatic backups are on (nightly plus a margin). */
export const OLD_AFTER_HOURS = 26;
/** Refresh of the status and the list while a run is in progress, and otherwise (milliseconds). */
export const RUNNING_REFRESH_MS = 15_000;
export const IDLE_REFRESH_MS = 60_000;

export type BackupState = "ok" | "old" | "never" | "manual";
export type BackupStateIcon = "check" | "alert" | "none" | "clock";

const LOOK: Record<BackupState, { tone: Tone; icon: BackupStateIcon }> = {
  ok: { tone: "success", icon: "check" },
  old: { tone: "danger", icon: "alert" },
  never: { tone: "danger", icon: "none" },
  manual: { tone: "neutral", icon: "clock" },
};

/** Colour family and icon of a state: colour, icon and text always travel together. */
export const backupStateLook = (state: BackupState) => LOOK[state];

const num = (value: number | string | null | undefined): number | null => {
  if (value === null || value === undefined || value === "") return null;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : null;
};

type StatusLike = { enabled: boolean; lastVerifiedAt?: string | null; lastVerifiedAgeHours?: number | string | null };

/** Age of the newest verified backup in hours: the API's own figure, else computed from its time; null when there is none. */
export function ageHours(status: Pick<StatusLike, "lastVerifiedAt" | "lastVerifiedAgeHours">, now: Date): number | null {
  if (!status.lastVerifiedAt) return null;
  const fromApi = num(status.lastVerifiedAgeHours);
  if (fromApi !== null) return Math.max(0, fromApi);
  const t = new Date(status.lastVerifiedAt).getTime();
  return Number.isNaN(t) ? null : Math.max(0, (now.getTime() - t) / 3_600_000);
}

/**
 * Never: no verified backup at all. With automatic backups on: ok up to OLD_AFTER_HOURS, old beyond. With them off a backup has no
 * schedule to be late for, so it is shown as "manual" (neutral), never as green or as late.
 */
export function backupState(status: StatusLike, now: Date): BackupState {
  const age = ageHours(status, now);
  if (age === null) return "never";
  if (!status.enabled) return "manual";
  return age > OLD_AFTER_HOURS ? "old" : "ok";
}

/** "3 d 4 h", "2 h 5 min", "7 min", "42 s" from hours (two largest units). */
export function formatAge(hours: number, units: { d: string; h: string; min: string; s: string }): string {
  const total = Math.max(0, Math.floor(hours * 3600));
  const d = Math.floor(total / 86400);
  const h = Math.floor((total % 86400) / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (d > 0) return `${d} ${units.d} ${h} ${units.h}`;
  if (h > 0) return `${h} ${units.h} ${m} ${units.min}`;
  if (m > 0) return `${m} ${units.min}`;
  return `${s} ${units.s}`;
}

/** Bytes as "512 B", "1.5 KB", "8.0 MB", "2.3 GB" (1024 based); null or negative gives a dash. */
export function formatBytes(bytes: number | string | null | undefined): string {
  const value = num(bytes);
  if (value === null || value < 0) return "-";
  if (value < 1024) return `${Math.round(value)} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let size = value / 1024;
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(1)} ${units[unit]}`;
}

/** Always megabytes with one decimal (the data inventory's uploads line): "12.3 MB". */
export function formatMegabytes(bytes: number | string | null | undefined): string {
  const value = num(bytes);
  return value === null || value < 0 ? "-" : `${(value / 1_048_576).toFixed(1)} MB`;
}

/** Seconds of a finished run as "45 s", "2 min 5 s"; null (still running or unknown) gives a dash. */
export function formatRunDuration(seconds: number | string | null | undefined, units: { min: string; s: string }): string {
  const value = num(seconds);
  if (value === null || value < 0) return "-";
  const total = Math.floor(value);
  const m = Math.floor(total / 60);
  return m > 0 ? `${m} ${units.min} ${total % 60} ${units.s}` : `${total} ${units.s}`;
}

export type RunStatus = "Running" | "Succeeded" | "Failed";
export const RUN_TONE: Record<RunStatus, Tone> = { Running: "info", Succeeded: "success", Failed: "danger" };

/** A run counts as "done and good" only when it succeeded AND verified (the API never says Succeeded otherwise). */
export const isGoodRun = (run: { status: string; verified: boolean }): boolean => run.status === "Succeeded" && run.verified;

/** True while the server says a backup is running, or the list shows a Running row. */
export function anyRunning(status: { running?: boolean } | undefined, items: readonly { status: string }[] | undefined): boolean {
  return !!status?.running || !!items?.some((r) => r.status === "Running");
}

/**
 * Polling interval: fast while a run is in progress, slow otherwise, none while the tab is hidden (the next refresh happens when the
 * tab is shown again).
 */
export function pollInterval(running: boolean, tabVisible: boolean): number | false {
  if (!tabVisible) return false;
  return running ? RUNNING_REFRESH_MS : IDLE_REFRESH_MS;
}

export type StartFailure = "running" | "forbidden" | "other";

/** What a failed "Back up now" means for the screen: 409 SYSTEM.BACKUP_RUNNING is calm news, not an error. */
export function startFailure(problem: { status?: number; code?: string } | undefined | null): StartFailure {
  if (!problem) return "other";
  if (problem.code === "SYSTEM.BACKUP_RUNNING" || problem.status === 409) return "running";
  if (problem.status === 403 || problem.code === "AUTH.FORBIDDEN") return "forbidden";
  return "other";
}

/** Inventory keys the API sends, in the order the card shows them; a key the screen does not know is still listed after these. */
export const INVENTORY_KEYS = ["customers", "orders", "products", "variants", "stockLevels", "invoices", "payments", "auditLogs"] as const;
export type InventoryKey = (typeof INVENTORY_KEYS)[number];

export const isKnownInventoryKey = (key: string): key is InventoryKey => (INVENTORY_KEYS as readonly string[]).includes(key);

/** Rows in the fixed display order, unknown keys last (kept, never hidden). */
export function orderInventory<T extends { key: string }>(rows: readonly T[]): T[] {
  const rank = (key: string) => {
    const i = (INVENTORY_KEYS as readonly string[]).indexOf(key);
    return i === -1 ? INVENTORY_KEYS.length : i;
  };
  return [...rows].sort((a, b) => rank(a.key) - rank(b.key));
}
