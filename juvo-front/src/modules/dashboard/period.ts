import { addDays, isIsoDate, num, presetRange, rangeProblem, spanDays, type RangeProblem } from "@/modules/reports/logic";

/**
 * Pure rules of the dashboard's date range (FR-M08), the previous-period comparison (FR-M03), the links behind the cards and the
 * small reshaping of report rows (FR-M04..M07). No React in here: every rule has a unit test (tests/dashboard.test.ts).
 */

export const GROUPS = ["day", "week", "month"] as const;
export type Group = (typeof GROUPS)[number];

export type DashboardRange = { from: string; to: string; group: Group; compare: boolean };

type Params = { get(name: string): string | null };

/** A readable default grouping for a range: days for a month or less, weeks up to half a year, months beyond. */
export function defaultGroup(from: string, to: string): Group {
  const span = spanDays(from, to);
  return span <= 45 ? "day" : span <= 183 ? "week" : "month";
}

/** The range from the address (a shared link). Anything missing or wrong falls back to the last 30 days; compare is on unless `compare=0`. */
export function parseRange(params: Params, today: string): DashboardRange {
  const fallback = presetRange("last30", today);
  const from = params.get("from");
  const to = params.get("to");
  const ok = isIsoDate(from) && isIsoDate(to);
  const range = { from: ok ? from : fallback.from, to: ok ? to : fallback.to };
  const group = params.get("groupBy");
  return {
    ...range,
    group: (GROUPS as readonly string[]).includes(group ?? "") ? (group as Group) : defaultGroup(range.from, range.to),
    compare: params.get("compare") !== "0",
  };
}

/** The address query of a range: both dates always, the grouping and the compare switch only when they differ from the defaults. */
export function rangeToSearch(range: DashboardRange): string {
  const q = new URLSearchParams();
  q.set("from", range.from);
  q.set("to", range.to);
  if (range.group !== defaultGroup(range.from, range.to)) q.set("groupBy", range.group);
  if (!range.compare) q.set("compare", "0");
  return q.toString();
}

/** The checks the API makes, for the range and its grouping (day grouping is limited to 366 days). */
export function periodProblem(range: DashboardRange): RangeProblem | null {
  return rangeProblem(range.from, range.to, range.group);
}

/** The period of the same length that ends the day before this one starts. */
export function previousRange(from: string, to: string): { from: string; to: string } {
  const span = spanDays(from, to);
  const end = addDays(from, -1);
  return { from: addDays(end, -(span - 1)), to: end };
}

/** Change against the previous period: the difference, and the percent (one decimal) or null when there is nothing to compare with. */
export function delta(current: number, previous: number): { diff: number; percent: number | null } {
  return { diff: Math.round((current - previous) * 100) / 100, percent: previous === 0 ? null : Math.round(((current - previous) / previous) * 1000) / 10 };
}

/** Puts the previous period's value on each point of the current period by position (the n-th day, week or month). A shorter previous series leaves gaps. */
export function alignSeries<T extends { label: string; net: number }>(current: T[], previous: { net: number }[] | null): (T & { previous: number | null })[] {
  return current.map((p, i) => ({ ...p, previous: previous ? (previous[i]?.net ?? null) : null }));
}

/** The best rows by one figure, biggest first (ties keep the API order). */
export function topBy<T>(rows: T[], value: (row: T) => number, count: number): T[] {
  return rows
    .map((row, index) => ({ row, index, v: value(row) }))
    .sort((a, b) => b.v - a.v || a.index - b.index)
    .slice(0, count)
    .map((x) => x.row);
}

type PayRow = { key: string; orders: number | string; total: number | string };

/** Sales of the range split by payment: "COD" is cash on delivery, every other method is online. Orders placed, not payments confirmed. */
export function splitByPayment(rows: PayRow[]): { online: { orders: number; total: number }; cod: { orders: number; total: number } } {
  const out = { online: { orders: 0, total: 0 }, cod: { orders: 0, total: 0 } };
  for (const r of rows) {
    const side = r.key === "COD" ? out.cod : out.online;
    side.orders += num(r.orders);
    side.total += num(r.total);
  }
  out.online.total = Math.round(out.online.total * 100) / 100;
  out.cod.total = Math.round(out.cod.total * 100) / 100;
  return out;
}

type LowRow = { available: number | string };

/** Stock health from the report: items at or under their reorder level, and among them the ones with nothing left to sell. `exact` is false when the list was cut. */
export function stockHealth(lowStockCount: number | string, lowStock: LowRow[]): { low: number; out: number; exact: boolean } {
  const low = num(lowStockCount);
  return { low, out: lowStock.filter((r) => num(r.available) <= 0).length, exact: lowStock.length >= low };
}

/* ---- Links behind the cards ------------------------------------------------------------------------------------------------- */

export const ORDER_STATUSES = ["Placed", "Confirmed", "Packed", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered", "Cancelled", "DeliveryFailed", "ReturnInProgress", "Returned"] as const;
export type OrderStatusKey = (typeof ORDER_STATUSES)[number];

export const isOrderStatus = (value: string | null | undefined): value is OrderStatusKey => !!value && (ORDER_STATUSES as readonly string[]).includes(value);

/** The orders list with the filters it really accepts: one status and a day range. Both are optional. */
export function ordersLink(filter: { status?: string; from?: string; to?: string } = {}): string {
  const q = new URLSearchParams();
  if (isOrderStatus(filter.status)) q.set("status", filter.status);
  if (isIsoDate(filter.from) && isIsoDate(filter.to)) {
    q.set("from", filter.from);
    q.set("to", filter.to);
  }
  const s = q.toString();
  return s ? `/admin/orders?${s}` : "/admin/orders";
}

/** Counts of the dashboard's orders-by-status, grouped for the cards. */
export function statusCounts(rows: { status: string; count: number | string }[]): { inDelivery: number; delivered: number; cancelled: number; returned: number; failed: number } {
  const by = new Map(rows.map((r) => [r.status, num(r.count)]));
  const get = (k: string) => by.get(k) ?? 0;
  return {
    inDelivery: get("HandedToCourier") + get("InTransit") + get("OutForDelivery"),
    delivered: get("Delivered"),
    cancelled: get("Cancelled"),
    returned: get("Returned") + get("ReturnInProgress"),
    failed: get("DeliveryFailed"),
  };
}
