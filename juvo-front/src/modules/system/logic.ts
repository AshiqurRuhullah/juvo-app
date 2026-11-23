/**
 * Pure rules of the background jobs and integration health screens (FR-AF02, FR-AF04; docs/system-api.md). No React, no network:
 * status to badge mapping, filter to query building, selection limits and bulk result summaries live here so they can be unit tested.
 */

/** The API accepts 1 to 100 distinct ids in one bulk call (400 SYSTEM.TOO_MANY above). */
export const MAX_BULK = 100;
/** The status panel and the integration cards refresh themselves this often (milliseconds). */
export const REFRESH_MS = 30_000;

export type IntegrationStatus = "Healthy" | "Degraded" | "Down" | "Disabled" | "Sandbox";
/** What the screen shows. `Unknown` is our own honest state: the API says Healthy, but no call has been recorded since the server started. */
export type ShownStatus = IntegrationStatus | "Unknown";
export type Tone = "success" | "warning" | "danger" | "neutral" | "info";
export type StatusIcon = "check" | "alert" | "down" | "off" | "flask" | "unknown";

type HealthLike = { status: IntegrationStatus; lastSuccessAt?: string | null; lastFailureAt?: string | null };

/** True when nothing was ever recorded for the slot (no success and no failure). */
export const hasNoCalls = (row: Pick<HealthLike, "lastSuccessAt" | "lastFailureAt">): boolean => !row.lastSuccessAt && !row.lastFailureAt;

/**
 * Never green for something not recorded: a live slot reported Healthy with no recorded call is shown as Unknown. A slot the API
 * marks Degraded or Down always has a failure, so it is shown as is.
 */
export function shownStatus(row: HealthLike): ShownStatus {
  if (row.status === "Healthy" && hasNoCalls(row)) return "Unknown";
  return row.status;
}

const LOOK: Record<ShownStatus, { tone: Tone; icon: StatusIcon }> = {
  Healthy: { tone: "success", icon: "check" },
  Degraded: { tone: "warning", icon: "alert" },
  Down: { tone: "danger", icon: "down" },
  Disabled: { tone: "neutral", icon: "off" },
  Sandbox: { tone: "info", icon: "flask" },
  Unknown: { tone: "neutral", icon: "unknown" },
};

/** Colour family and icon of a status: colour, icon and text always travel together. */
export const statusLook = (status: ShownStatus) => LOOK[status];

export type JobOutcome = "Running" | "Succeeded" | "Failed";
export const JOB_OUTCOMES: JobOutcome[] = ["Running", "Succeeded", "Failed"];
export const OUTCOME_TONE: Record<JobOutcome, Tone> = { Running: "info", Succeeded: "success", Failed: "danger" };

export type OutboxState = "Pending" | "Retrying" | "DeadLettered";
export const OUTBOX_STATES: OutboxState[] = ["Pending", "Retrying", "DeadLettered"];
export const STATE_TONE: Record<OutboxState, Tone> = { Pending: "info", Retrying: "warning", DeadLettered: "danger" };

/** The API requeues only a dead-lettered or a retrying message; a fresh Pending one answers 409 SYSTEM.NOT_REQUEUEABLE. */
export const canRequeue = (state: OutboxState): boolean => state === "DeadLettered" || state === "Retrying";

export type Locale = "en" | "bn";

/** Start of the first day and start of the day after the last one (the API's `to` is exclusive), as ISO instants. */
export function dayRange(from: string | undefined, to: string | undefined): { from?: string; to?: string } {
  const start = from ? new Date(`${from}T00:00:00`) : null;
  const end = to ? new Date(`${to}T00:00:00`) : null;
  if (end) end.setDate(end.getDate() + 1);
  return { from: start && !Number.isNaN(start.getTime()) ? start.toISOString() : undefined, to: end && !Number.isNaN(end.getTime()) ? end.toISOString() : undefined };
}

const text = (value: string | undefined): string | undefined => {
  const trimmed = value?.trim();
  return trimmed ? trimmed : undefined;
};

export type RunFilters = { job?: string; outcome?: string; from?: string; to?: string; reference?: string };
export type PageArgs = { page: number; pageSize: number; search?: string };

/** Query of `GET /system/jobs`: empty filters are left out, dates become a half-open instant range. */
export function runQuery(page: PageArgs, f: RunFilters) {
  const query: Record<string, string | number> = { Page: page.page, PageSize: page.pageSize };
  const search = text(page.search);
  if (search) query.Search = search;
  if (f.job) query.job = f.job;
  if (f.outcome) query.outcome = f.outcome;
  const reference = text(f.reference);
  if (reference) query.reference = reference;
  const range = dayRange(f.from, f.to);
  if (range.from) query.from = range.from;
  if (range.to) query.to = range.to;
  return query;
}

export type OutboxFilters = { state?: string; type?: string; from?: string; to?: string };

/** Query of `GET /system/jobs/outbox`. */
export function outboxQuery(page: PageArgs, f: OutboxFilters) {
  const query: Record<string, string | number> = { Page: page.page, PageSize: page.pageSize };
  const search = text(page.search);
  if (search) query.Search = search;
  if (f.state) query.state = f.state;
  const type = text(f.type);
  if (type) query.type = type;
  const range = dayRange(f.from, f.to);
  if (range.from) query.from = range.from;
  if (range.to) query.to = range.to;
  return query;
}

export type FailedFilters = { channel?: string; template?: string; from?: string; to?: string };

/** Query of `GET /system/jobs/notifications/failed`. */
export function failedQuery(page: PageArgs, f: FailedFilters) {
  const query: Record<string, string | number> = { Page: page.page, PageSize: page.pageSize };
  const search = text(page.search);
  if (search) query.Search = search;
  if (f.channel) query.channel = f.channel;
  const template = text(f.template);
  if (template) query.template = template;
  const range = dayRange(f.from, f.to);
  if (range.from) query.from = range.from;
  if (range.to) query.to = range.to;
  return query;
}

export const anyFilter = (filters: Record<string, string | undefined>, searchText: string): boolean => !!searchText.trim() || Object.values(filters).some((v) => !!v);

/** Selection that never holds more than MAX_BULK distinct ids. Returns a new set; `full` says an id was refused for the limit. */
export function toggleId(selected: ReadonlySet<string>, id: string, limit: number = MAX_BULK): { next: Set<string>; full: boolean } {
  const next = new Set(selected);
  if (next.has(id)) {
    next.delete(id);
    return { next, full: false };
  }
  if (next.size >= limit) return { next, full: true };
  next.add(id);
  return { next, full: false };
}

/** Adds or removes a whole page of ids; when the limit is reached the rest of the page is left out and `full` is true. */
export function togglePage(selected: ReadonlySet<string>, ids: readonly string[], on: boolean, limit: number = MAX_BULK): { next: Set<string>; full: boolean } {
  const next = new Set(selected);
  if (!on) {
    ids.forEach((id) => next.delete(id));
    return { next, full: false };
  }
  let full = false;
  for (const id of ids) {
    if (next.has(id)) continue;
    if (next.size >= limit) {
      full = true;
      break;
    }
    next.add(id);
  }
  return { next, full };
}

/** State of the header checkbox for one page of selectable ids. */
export function pageCheck(selected: ReadonlySet<string>, ids: readonly string[]): boolean | "indeterminate" {
  if (ids.length === 0) return false;
  const count = ids.filter((id) => selected.has(id)).length;
  return count === 0 ? false : count === ids.length ? true : "indeterminate";
}

export type RequeueResult = { requested: number | string; requeued: number | string; skipped: { id: string; reason: string }[] };
export type ResultSummary = { requested: number; requeued: number; skipped: { id: string; reason: string; reasonKey: string | null }[]; outcome: "all" | "some" | "none" };

/** Known reasons of the API (English sentences, docs/system-api.md) mapped to message keys; anything else is shown as the API sent it. */
const REASONS: [RegExp, string][] = [
  [/^not found/i, "notFound"],
  [/already delivered/i, "alreadyDelivered"],
  [/already pending, sent or skipped/i, "notFailed"],
];

export const reasonKey = (reason: string): string | null => REASONS.find(([pattern]) => pattern.test(reason))?.[1] ?? null;

/** Counts and per-item reasons of a bulk answer; the API sends counts as number or string. */
export function summariseResult(result: RequeueResult): ResultSummary {
  const requested = Number(result.requested);
  const requeued = Number(result.requeued);
  return {
    requested,
    requeued,
    skipped: result.skipped.map((s) => ({ id: s.id, reason: s.reason, reasonKey: reasonKey(s.reason) })),
    outcome: requeued <= 0 ? "none" : requeued >= requested ? "all" : "some",
  };
}

const num = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));

/** "3 d 4 h", "2 h 5 min", "7 min", "42 s": the two largest units. */
export function formatSpan(seconds: number | string | null | undefined, units: { d: string; h: string; min: string; s: string }): string {
  const total = Math.max(0, Math.floor(num(seconds)));
  const d = Math.floor(total / 86400);
  const h = Math.floor((total % 86400) / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (d > 0) return `${d} ${units.d} ${h} ${units.h}`;
  if (h > 0) return `${h} ${units.h} ${m} ${units.min}`;
  if (m > 0) return `${m} ${units.min}`;
  return `${s} ${units.s}`;
}

/** Milliseconds as "420 ms" or "1.3 s". */
export function formatMs(ms: number | string | null | undefined, seconds = "s"): string {
  const value = num(ms);
  return value >= 1000 ? `${(value / 1000).toFixed(1)} ${seconds}` : `${value} ms`;
}

/** The jobs the summary knows, as filter options (sorted, distinct). */
export const jobNames = (summary: readonly { jobName: string }[]): string[] => [...new Set(summary.map((s) => s.jobName))].sort((a, b) => a.localeCompare(b));
