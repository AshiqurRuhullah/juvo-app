/**
 * SAMPLE DATA for the dashboard. Nothing here comes from the database: orders, payments and couriers arrive in Steps 8 to 10, and
 * until then this keeps the dashboard looking like the finished product. Replace the contents of `buildSampleDashboard` with calls
 * to the real reporting endpoints (and keep the shape of `DashboardData`) when they exist; the view does not change.
 */
export type TrendKey = "sales" | "orders" | "average" | "customers";

export type Kpi = {
  key: TrendKey;
  /** Today's value (money in BDT for sales and average, a count otherwise). */
  value: number;
  /** Change against yesterday, in percent. */
  change: number;
  /** The last seven days, oldest first, for the sparkline. */
  spark: number[];
};

export type DayPoint = { date: string; sales: number; orders: number };

export type StatusKey = "placed" | "confirmed" | "packed" | "inTransit" | "delivered" | "returned";

export type ActionKey = "confirm" | "verifyPayments" | "lowStock" | "returns" | "tickets";

export type RecentOrder = {
  id: string;
  number: string;
  customer: string;
  total: number;
  payment: "paid" | "cod" | "review";
  delivery: "placed" | "packed" | "inTransit" | "delivered";
  createdAt: string;
};

export type DashboardData = {
  kpis: Kpi[];
  days: DayPoint[];
  statuses: { key: StatusKey; count: number }[];
  actions: { key: ActionKey; count: number; href?: string }[];
  topProducts: { name: string; units: number; revenue: number }[];
  cash: { collectedToday: number; codWithCouriers: number; onlineToday: number; refundsPending: number };
  stock: { healthy: number; low: number; out: number };
  recent: RecentOrder[];
};

const SALES = [48200, 51800, 46500, 62300, 71900, 68400, 55100, 59800, 63200, 70100, 76400, 81200, 74800, 86350];
const ORDERS = [31, 34, 29, 40, 46, 44, 36, 39, 41, 45, 49, 52, 48, 55];
const CUSTOMERS = [11, 14, 9, 16, 19, 17, 12, 15, 14, 18, 21, 17, 19, 18];

const pct = (today: number, yesterday: number) => Math.round(((today - yesterday) / yesterday) * 1000) / 10;

/** `todayIso` is the day the server rendered the page, so server and browser draw the same dates. */
export function buildSampleDashboard(todayIso: string): DashboardData {
  const today = new Date(todayIso);
  const day = (offset: number) => new Date(today.getTime() - offset * 86_400_000).toISOString();
  const minutesAgo = (minutes: number) => new Date(today.getTime() - minutes * 60_000).toISOString();

  const last = SALES.length - 1;
  const average = SALES.map((s, i) => Math.round(s / ORDERS[i]!));

  return {
    kpis: [
      { key: "sales", value: SALES[last]!, change: pct(SALES[last]!, SALES[last - 1]!), spark: SALES.slice(-7) },
      { key: "orders", value: ORDERS[last]!, change: pct(ORDERS[last]!, ORDERS[last - 1]!), spark: ORDERS.slice(-7) },
      { key: "average", value: average[last]!, change: pct(average[last]!, average[last - 1]!), spark: average.slice(-7) },
      { key: "customers", value: CUSTOMERS[last]!, change: pct(CUSTOMERS[last]!, CUSTOMERS[last - 1]!), spark: CUSTOMERS.slice(-7) },
    ],
    days: SALES.map((sales, i) => ({ date: day(last - i), sales, orders: ORDERS[i]! })),
    statuses: [
      { key: "placed", count: 12 },
      { key: "confirmed", count: 18 },
      { key: "packed", count: 9 },
      { key: "inTransit", count: 21 },
      { key: "delivered", count: 94 },
      { key: "returned", count: 4 },
    ],
    actions: [
      { key: "confirm", count: 12 },
      { key: "verifyPayments", count: 3 },
      { key: "lowStock", count: 17, href: "/admin/inventory" },
      { key: "returns", count: 2 },
      { key: "tickets", count: 5 },
    ],
    topProducts: [
      { name: "Classic Cotton T-Shirt", units: 148, revenue: 96200 },
      { name: "Everyday Canvas Sneakers", units: 96, revenue: 235200 },
      { name: "Floral Summer Dress", units: 84, revenue: 192360 },
      { name: "Embroidered Cotton Kurti", units: 77, revenue: 150150 },
      { name: "Mini Crossbody Bag", units: 63, revenue: 119070 },
    ],
    cash: { collectedToday: 48200, codWithCouriers: 212400, onlineToday: 38150, refundsPending: 3200 },
    stock: { healthy: 128, low: 17, out: 5 },
    recent: [
      { id: "1", number: "JV-HO-2611-000123", customer: "Nusrat Jahan", total: 2290, payment: "paid", delivery: "packed", createdAt: minutesAgo(9) },
      { id: "2", number: "JV-HO-2611-000122", customer: "Rafiq Ahmed", total: 3900, payment: "cod", delivery: "placed", createdAt: minutesAgo(26) },
      { id: "3", number: "JV-ONL-2611-000121", customer: "Tania Sultana", total: 1650, payment: "review", delivery: "placed", createdAt: minutesAgo(48) },
      { id: "4", number: "JV-HO-2611-000120", customer: "Mahmud Hasan", total: 1450, payment: "paid", delivery: "inTransit", createdAt: minutesAgo(95) },
      { id: "5", number: "JV-ONL-2611-000119", customer: "Sadia Rahman", total: 5900, payment: "cod", delivery: "inTransit", createdAt: minutesAgo(140) },
      { id: "6", number: "JV-HO-2611-000118", customer: "Imran Hossain", total: 950, payment: "paid", delivery: "delivered", createdAt: minutesAgo(210) },
    ],
  };
}
