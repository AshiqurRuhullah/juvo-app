/**
 * Pure logic of pre-orders (FR-AA01..AA03): the label and ship date a customer sees, the order-level state text, the allocate result
 * and the product settings form. No React and no network, unit-tested in tests/preorders.test.ts.
 */

type Num = number | string | null | undefined;

const num = (value: Num): number => {
  if (value === null || value === undefined || value === "") return 0;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
};

export type PreorderFacts = { isPreorder?: boolean | null; preorderExpectedShipDate?: string | null };

export type PreorderLabel = { kind: "dated"; date: string } | { kind: "undated" } | null;

/** "Pre-order" with the expected ship date, or without it ("ships when it is back in stock"). Null when the line is not a pre-order. */
export function preorderLabel(facts: PreorderFacts | null | undefined): PreorderLabel {
  if (!facts?.isPreorder) return null;
  const date = (facts.preorderExpectedShipDate ?? "").trim();
  return date ? { kind: "dated", date } : { kind: "undated" };
}

/** A calendar date from the API ("2026-12-20") in the page language. The date has no time zone, so it is read and printed in UTC. */
export function formatShipDate(date: string, locale: string): string {
  const parsed = new Date(`${date.slice(0, 10)}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime())) return date;
  return new Intl.DateTimeFormat(locale === "bn" ? "bn-BD" : "en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" }).format(parsed);
}

/** The earliest expected ship date among the given pre-order facts (several lines), or null when none has one. */
export function earliestShipDate(items: PreorderFacts[]): string | null {
  const dates = items.filter((i) => i.isPreorder && i.preorderExpectedShipDate).map((i) => (i.preorderExpectedShipDate as string).slice(0, 10));
  return dates.length > 0 ? dates.sort()[0]! : null;
}

/** The latest expected ship date (the whole order ships when its last pre-order item arrives). */
export function latestShipDate(items: PreorderFacts[]): string | null {
  const dates = items.filter((i) => i.isPreorder && i.preorderExpectedShipDate).map((i) => (i.preorderExpectedShipDate as string).slice(0, 10));
  return dates.length > 0 ? dates.sort()[dates.length - 1]! : null;
}

export type PreorderStateName = "None" | "Waiting" | "Ready";

/** Which explanation a customer sees for an order: waiting for stock, ready (stock arrived), or nothing. */
export function orderStateNotice(state: string | null | undefined): "waiting" | "ready" | null {
  if (state === "Waiting") return "waiting";
  if (state === "Ready") return "ready";
  return null;
}

export type AllocateResult = { linesAllocated: number | string; ordersReady: number | string; readyOrderIds?: string[] };

/** The words for the answer of "give stock to waiting lines": nothing to serve, or some lines and orders. */
export function allocateSummary(result: AllocateResult): { kind: "none" } | { kind: "some"; lines: number; orders: number } {
  const lines = num(result.linesAllocated);
  const orders = num(result.ordersReady);
  return lines <= 0 && orders <= 0 ? { kind: "none" } : { kind: "some", lines, orders };
}

/* ---- Product settings form ------------------------------------------------------------------------------------------------- */

export type PreorderDraft = { allow: "inherit" | "yes" | "no"; date: string; percent: string };

export type PreorderSettingsLike = { allowPreorder: boolean | null; expectedShipDate: string | null; advancePercent: Num | null };

export const emptyPreorderDraft = (allow: PreorderDraft["allow"] = "no"): PreorderDraft => ({ allow, date: "", percent: "" });

/** A form draft from what the API holds (the product switch is a plain boolean, a variant's may be inherited = null). */
export function draftFromSettings(settings: PreorderSettingsLike, isVariant: boolean): PreorderDraft {
  const allow: PreorderDraft["allow"] = settings.allowPreorder === null ? (isVariant ? "inherit" : "no") : settings.allowPreorder ? "yes" : "no";
  const percent = settings.advancePercent === null || settings.advancePercent === undefined ? "" : String(num(settings.advancePercent));
  return { allow, date: settings.expectedShipDate ? settings.expectedShipDate.slice(0, 10) : "", percent };
}

export type PreorderErrors = { date?: "past" | "far" | "invalid"; percent?: "range" | "invalid" };

/** The same day two years later (the API accepts an expected ship date up to two years ahead). */
export function twoYearsAhead(today: string): string {
  const year = Number(today.slice(0, 4)) + 2;
  return `${year}${today.slice(4)}`;
}

/** Checks the form: the advance is a percentage from 0 to 100, the date a real calendar date from today up to two years ahead. */
export function validatePreorderDraft(draft: PreorderDraft, today: string): PreorderErrors {
  const errors: PreorderErrors = {};
  if (draft.percent.trim() !== "") {
    const n = Number(draft.percent);
    if (!Number.isFinite(n)) errors.percent = "invalid";
    else if (n < 0 || n > 100) errors.percent = "range";
  }
  if (draft.date.trim() !== "") {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(draft.date) || Number.isNaN(Date.parse(`${draft.date}T00:00:00Z`))) errors.date = "invalid";
    else if (draft.date < today) errors.date = "past";
    else if (draft.date > twoYearsAhead(today)) errors.date = "far";
  }
  return errors;
}

/** The request body. `allow: "inherit"` (variants only) sends null so the product's setting counts; a blank date or percent clears them. */
export function draftToRequest(draft: PreorderDraft): { allowPreorder: boolean | null; expectedShipDate: string | null; advancePercent: number | null } {
  return {
    allowPreorder: draft.allow === "inherit" ? null : draft.allow === "yes",
    expectedShipDate: draft.date.trim() === "" ? null : draft.date,
    advancePercent: draft.percent.trim() === "" ? null : Number(draft.percent),
  };
}

export const sameDraft = (a: PreorderDraft, b: PreorderDraft) => a.allow === b.allow && a.date === b.date && a.percent === b.percent;

/** Today's date in Dhaka (the business time zone) as yyyy-mm-dd, for the "not in the past" rule. */
export function dhakaToday(now: number = Date.now()): string {
  return new Date(now + 6 * 3_600_000).toISOString().slice(0, 10);
}

/* ---- Queue ----------------------------------------------------------------------------------------------------------------- */

/** How many lines of a queue order still wait for stock. */
export function waitingLineCount(lines: { waiting: Num }[]): number {
  return lines.filter((l) => num(l.waiting) > 0).length;
}
