import { num } from "@/modules/reports/logic";

/** Pure rules of the dashboard cards (unit tested in tests/reports.test.ts). */

export type KpiKey = "sales" | "orders" | "average" | "toConfirm";
export type Kpi = { key: KpiKey; value: number; change: number | null; spark: number[] };

type Point = { sales: number | string; orders: number | string };

/** Percent change against yesterday, one decimal; null when yesterday had nothing to compare with. */
export function changeFrom(today: number, yesterday: number): number | null {
  return yesterday === 0 ? null : Math.round(((today - yesterday) / yesterday) * 1000) / 10;
}

/** The four cards from the real series (oldest first, today last): sales, orders and average order value against yesterday, and orders waiting for confirmation. */
export function buildKpis(series: Point[], todayTotals: Point, toConfirm: number | string): Kpi[] {
  const sales = series.map((p) => num(p.sales));
  const orders = series.map((p) => num(p.orders));
  const average = sales.map((s, i) => (orders[i]! > 0 ? Math.round((s / orders[i]!) * 100) / 100 : 0));
  const last = sales.length - 1;
  const change = (values: number[]) => (last > 0 ? changeFrom(values[last]!, values[last - 1]!) : null);
  return [
    { key: "sales", value: num(todayTotals.sales), change: change(sales), spark: sales.slice(-7) },
    { key: "orders", value: num(todayTotals.orders), change: change(orders), spark: orders.slice(-7) },
    { key: "average", value: average[last] ?? 0, change: change(average), spark: average.slice(-7) },
    { key: "toConfirm", value: num(toConfirm), change: null, spark: [] },
  ];
}
