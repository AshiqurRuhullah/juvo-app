/**
 * Pure rules of the report screens: Bangladesh dates, presets, range limits (the same as the API), the filters in the address bar,
 * number shaping for tables and charts, export addresses and which exports a person may use. No React in here, so every rule has a
 * unit test (tests/reports.test.ts). The API proves everything again: docs/reports-api.md is the contract.
 */

export const REPORT_NAMES = ["sales", "orders", "inventory", "finance", "courier", "customers"] as const;
export type ReportName = (typeof REPORT_NAMES)[number];

export const isReportName = (value: string): value is ReportName => (REPORT_NAMES as readonly string[]).includes(value);

export const viewPermission = (report: ReportName) => `reports.${report}.view`;
export const exportPermission = (report: ReportName) => `reports.${report}.export`;

/** Reports that offer `groupBy`, first value is the default (the API's own order). */
export const GROUP_BY = {
  sales: ["day", "week", "month", "product", "category", "size", "color", "channel", "zone", "payment"],
  finance: ["day", "week", "month"],
  pnl: ["day", "week", "month"],
} as const;
export type GroupByReport = keyof typeof GROUP_BY;

/** Sections a CSV or Excel export can hold (the PDF holds all). Reports without a list have one table. */
export const SECTIONS: Partial<Record<ReportName, readonly string[]>> = {
  orders: ["funnel", "cancellations", "returns"],
  inventory: ["summary", "low", "movements", "dead"],
  finance: ["finance", "cod"],
  customers: ["summary", "top", "conversion"],
};

export const ACCOUNTANT_KINDS = ["daybook", "expenses", "pnl", "payables", "purchases"] as const;
export type AccountantKind = (typeof ACCOUNTANT_KINDS)[number];
/** These two read the cost of goods: the API answers 403 REPORT.COST_HIDDEN without cost access. */
export const COST_KINDS: readonly AccountantKind[] = ["pnl", "purchases"];
/** The payables list is a snapshot of today: the range is ignored. */
export const kindUsesRange = (kind: AccountantKind) => kind !== "payables";

/** The API's limits (ReportAccess): daily grouping 366 days, anything else 1,830. Accountant export: 366. */
export const MAX_DAYS_DAILY = 366;
export const MAX_DAYS = 1830;

/* ---- Bangladesh dates ------------------------------------------------------------------------------------------------------- */

const DAY = 86_400_000;
const DATE_RE = /^(\d{4})-(\d{2})-(\d{2})$/;

/** True for a real calendar date written 2026-11-16. */
export function isIsoDate(value: string | null | undefined): value is string {
  if (!value) return false;
  const m = DATE_RE.exec(value);
  if (!m) return false;
  const [y, mo, d] = [Number(m[1]), Number(m[2]), Number(m[3])];
  const date = new Date(Date.UTC(y, mo - 1, d));
  return date.getUTCFullYear() === y && date.getUTCMonth() === mo - 1 && date.getUTCDate() === d;
}

const toUtc = (iso: string) => {
  const m = DATE_RE.exec(iso)!;
  return Date.UTC(Number(m[1]), Number(m[2]) - 1, Number(m[3]));
};
const fromUtc = (ms: number) => new Date(ms).toISOString().slice(0, 10);

/** The calendar day in Bangladesh (UTC+6, no daylight saving) for an instant. */
export function bangladeshToday(now: Date = new Date()): string {
  return fromUtc(now.getTime() + 6 * 3_600_000);
}

export function addDays(iso: string, days: number): string {
  return fromUtc(toUtc(iso) + days * DAY);
}

/** Days from `from` to `to` counting both ends (the API's span). */
export function spanDays(from: string, to: string): number {
  return Math.round((toUtc(to) - toUtc(from)) / DAY) + 1;
}

export const PRESETS = ["today", "last7", "last30", "thisMonth", "lastMonth"] as const;
export type Preset = (typeof PRESETS)[number];

export function presetRange(preset: Preset, today: string): { from: string; to: string } {
  const monthStart = `${today.slice(0, 7)}-01`;
  switch (preset) {
    case "today":
      return { from: today, to: today };
    case "last7":
      return { from: addDays(today, -6), to: today };
    case "last30":
      return { from: addDays(today, -29), to: today };
    case "thisMonth":
      return { from: monthStart, to: today };
    case "lastMonth": {
      const end = addDays(monthStart, -1);
      return { from: `${end.slice(0, 7)}-01`, to: end };
    }
  }
}

/** Which preset the range is, or "custom". */
export function detectPreset(from: string, to: string, today: string): Preset | "custom" {
  return PRESETS.find((p) => {
    const r = presetRange(p, today);
    return r.from === from && r.to === to;
  }) ?? "custom";
}

/* ---- Range checks ----------------------------------------------------------------------------------------------------------- */

export type RangeProblem = "missing" | "invalid" | "tooLarge";

/**
 * The same checks the API makes, so a wrong range is explained before a request is sent. `groupBy` is the effective grouping
 * ("day" when the report has none). `max` overrides the limit (the accountant export allows 366 days for every kind).
 */
export function rangeProblem(from: string | undefined, to: string | undefined, groupBy: string = "day", max?: number): RangeProblem | null {
  if (!isIsoDate(from) || !isIsoDate(to)) return "missing";
  if (from > to) return "invalid";
  const span = spanDays(from, to);
  const limit = max ?? (groupBy === "day" ? MAX_DAYS_DAILY : MAX_DAYS);
  return span > limit ? "tooLarge" : null;
}

/** The limit that applies, for the message. */
export const rangeLimit = (groupBy: string = "day", max?: number) => max ?? (groupBy === "day" ? MAX_DAYS_DAILY : MAX_DAYS);

/* ---- Filters in the address bar --------------------------------------------------------------------------------------------- */

export type ReportFilters = {
  from: string;
  to: string;
  /** Empty = consolidated (everything the person may see). */
  branchId?: string;
  groupBy?: string;
  days?: number;
};

type Params = { get(name: string): string | null };

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Filters from the address (a bookmark or a shared link). Anything missing or wrong falls back to the default: the last 30 days
 * ending today, the first grouping, 60 dead-stock days. `defaultBranchId` is used only when the link names no branch at all.
 */
export function parseFilters(params: Params, kind: GroupByReport | "plain", today: string, defaultBranchId?: string): ReportFilters {
  const fallback = presetRange("last30", today);
  const from = params.get("from");
  const to = params.get("to");
  const ok = isIsoDate(from) && isIsoDate(to);
  const branch = params.get("branch");
  const options: readonly string[] = kind === "plain" ? [] : GROUP_BY[kind];
  const groupBy = params.get("groupBy");
  const days = Number(params.get("days"));
  return {
    from: ok ? from : fallback.from,
    to: ok ? to : fallback.to,
    branchId: branch === null ? defaultBranchId : branch && UUID.test(branch) ? branch : undefined,
    groupBy: groupBy && options.includes(groupBy) ? groupBy : undefined,
    days: Number.isInteger(days) && days >= 1 && days <= 3650 ? days : undefined,
  };
}

/** The address query for the filters (always names both dates and the branch, so a link shows the same numbers to anyone). */
export function filtersToSearch(filters: ReportFilters): string {
  const q = new URLSearchParams();
  q.set("from", filters.from);
  q.set("to", filters.to);
  q.set("branch", filters.branchId ?? "");
  if (filters.groupBy) q.set("groupBy", filters.groupBy);
  if (filters.days) q.set("days", String(filters.days));
  return q.toString();
}

/** The effective grouping used by the range limit: the chosen one, or the report's default. */
export const effectiveGroupBy = (filters: ReportFilters) => filters.groupBy ?? "day";

/** Query parameters of the report endpoint. Empty values are left out. */
export function apiQuery(filters: ReportFilters, extra: Record<string, string | number | undefined> = {}): Record<string, string | number> {
  const entries: [string, string | number | undefined][] = [
    ["from", filters.from],
    ["to", filters.to],
    ["branchId", filters.branchId],
    ["groupBy", filters.groupBy],
    ["days", filters.days],
    ...Object.entries(extra),
  ];
  return Object.fromEntries(entries.filter(([, v]) => v !== undefined && v !== "")) as Record<string, string | number>;
}

/* ---- Numbers ---------------------------------------------------------------------------------------------------------------- */

/** The API sends numbers or numeric strings. */
export function num(value: number | string | null | undefined): number {
  if (value === null || value === undefined || value === "") return 0;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
}

/** Null stays null (a hidden or missing figure is never shown as 0). */
export const numOrNull = (value: number | string | null | undefined): number | null => (value === null || value === undefined ? null : num(value));

/** The API's rates are already percentages (12.5 means 12.5 %). Whole numbers lose the decimals. */
export function percentText(value: number | string | null | undefined, locale = "en"): string {
  const n = num(value);
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(n)}%`;
}

/** Hours split into days, hours and minutes for a short text ("2 d 4 h", "3 h 20 min", "45 min"); null when unknown. */
export function durationParts(hours: number | string | null | undefined): { days: number; hours: number; minutes: number } | null {
  if (hours === null || hours === undefined) return null;
  const total = Math.round(num(hours) * 60);
  if (total < 60 * 48) return { days: 0, hours: Math.floor(total / 60), minutes: total % 60 };
  return { days: Math.floor(total / 1440), hours: Math.round((total % 1440) / 60), minutes: 0 };
}

/** Cents-safe sum of money values. */
export function sum(values: (number | string | null | undefined)[]): number {
  return Math.round(values.reduce<number>((acc, v) => acc + Math.round(num(v) * 100), 0)) / 100;
}

/* ---- Shaping ---------------------------------------------------------------------------------------------------------------- */

export type FunnelStage = { stage: string; count: number | string; rateOfPlaced: number | string; rateOfPrevious: number | string };

/** Funnel rows ready to draw: the main stages in order, with a bar width relative to the placed orders; cancelled and returned apart. */
export function shapeFunnel(stages: FunnelStage[]) {
  const placed = stages.find((s) => s.stage === "Placed");
  const base = Math.max(num(placed?.count), 1);
  const rows = stages.map((s) => ({ stage: s.stage, count: num(s.count), rateOfPlaced: num(s.rateOfPlaced), rateOfPrevious: num(s.rateOfPrevious), width: Math.min(100, Math.round((num(s.count) / base) * 100)) }));
  return { main: rows.filter((r) => r.stage !== "Cancelled" && r.stage !== "Returned"), losses: rows.filter((r) => r.stage === "Cancelled" || r.stage === "Returned") };
}

export type SeriesPoint = { label: string; start?: string; netRevenue: number | string; deliveryCollected: number | string; costOfGoods?: number | string | null; grossProfit?: number | string | null; netProfit?: number | string | null; orders: number | string; expenses?: number | string };

/** Points for the finance and profit charts. Hidden cost stays `null` so the line is simply not drawn. */
export function shapeSeries(rows: SeriesPoint[]) {
  return rows.map((r) => ({
    label: r.label,
    revenue: num(r.netRevenue),
    delivery: num(r.deliveryCollected),
    profit: r.grossProfit === undefined || r.grossProfit === null ? null : num(r.grossProfit),
    net: r.netProfit === undefined || r.netProfit === null ? null : num(r.netProfit),
    orders: num(r.orders),
    expenses: r.expenses === undefined ? null : num(r.expenses),
  }));
}

export type SalesRow = { key: string; label: string; orders: number | string; units: number | string; net: number | string; total: number | string };

/** Chart data for the sales report: time groupings as ordered points; other groupings as the biggest rows first (at most `max`). */
export function shapeSales(rows: SalesRow[], groupBy: string, max = 12) {
  const timed = groupBy === "day" || groupBy === "week" || groupBy === "month";
  const points = rows.map((r) => ({ label: r.label, net: num(r.net), orders: num(r.orders), units: num(r.units) }));
  return timed ? points : [...points].sort((a, b) => b.net - a.net).slice(0, max);
}

/** Chart rows for courier rates: delivered, failed and returned percentages. */
export function shapeCourier(rows: { courier: string; deliveredRate: number | string; failedRate: number | string; returnedRate: number | string }[]) {
  return rows.map((r) => ({ label: r.courier, delivered: num(r.deliveredRate), failed: num(r.failedRate), returned: num(r.returnedRate) }));
}

/** Chart rows for stock value by branch. Cost is null when hidden. */
export function shapeStock(rows: { branchCode: string; valueAtPrice: number | string; valueAtCost: number | string | null }[]) {
  return rows.map((r) => ({ label: r.branchCode, price: num(r.valueAtPrice), cost: r.valueAtCost === null ? null : num(r.valueAtCost) }));
}

/* ---- Export ----------------------------------------------------------------------------------------------------------------- */

export type ExportFormat = "csv" | "xlsx" | "pdf";
export const EXPORT_FORMATS: readonly ExportFormat[] = ["csv", "xlsx", "pdf"];

/** Address (through the BFF) of one report export. `section` only applies to CSV and Excel; `lang` only to the PDF. */
export function exportUrl(report: ReportName, filters: ReportFilters, options: { format: ExportFormat; lang?: "en" | "bn"; section?: string }): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(apiQuery(filters, { format: options.format, lang: options.format === "pdf" ? (options.lang ?? "en") : undefined, section: options.format === "pdf" ? undefined : options.section }))) q.set(k, String(v));
  return `/api/bff/api/v1/reports/${report}/export?${q.toString()}`;
}

/** Address of the accountant export. The range is left out for the payables snapshot. */
export function accountantUrl(kind: AccountantKind, format: "csv" | "xlsx", filters: ReportFilters): string {
  const base = kindUsesRange(kind) ? apiQuery({ from: filters.from, to: filters.to, branchId: filters.branchId, groupBy: filters.groupBy }) : apiQuery({ from: filters.from, to: filters.to, branchId: filters.branchId });
  const q = new URLSearchParams();
  q.set("kind", kind);
  q.set("format", format);
  for (const [k, v] of Object.entries(base)) q.set(k, String(v));
  return `/api/bff/api/v1/accounting/export?${q.toString()}`;
}

/** The file name the server chose (Content-Disposition, plain or RFC 5987), or a fallback. Never a path. */
export function filenameFrom(disposition: string | null | undefined, fallback: string): string {
  if (!disposition) return fallback;
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/i.exec(disposition);
  let name: string | undefined;
  if (star) {
    try {
      name = decodeURIComponent(star[1]!.trim());
    } catch {
      name = undefined;
    }
  }
  if (!name) {
    const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(disposition);
    name = plain?.[1]?.trim();
  }
  if (!name) return fallback;
  const base = name.split(/[\\/]/).pop()!.replace(/[<>:"|?*\u0000-\u001f]/g, "_");
  return base || fallback;
}

/** Which formats this person may export for a report (empty = no Export menu). */
export function allowedFormats(canExport: boolean): readonly ExportFormat[] {
  return canExport ? EXPORT_FORMATS : [];
}

/** The reports a person may open, in menu order. `has` answers one permission code. */
export function openableReports(has: (code: string) => boolean): ReportName[] {
  return REPORT_NAMES.filter((r) => has(viewPermission(r)));
}

/** Accountant export kinds offered: the cost ones are still listed (the API answers with its reason) but flagged. */
export const costKind = (kind: AccountantKind) => COST_KINDS.includes(kind);
