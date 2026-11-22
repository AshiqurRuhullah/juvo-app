import { describe, expect, it } from "vitest";
import { alignSeries, defaultGroup, delta, ordersLink, parseRange, periodProblem, previousRange, rangeToSearch, splitByPayment, statusCounts, stockHealth, topBy } from "@/modules/dashboard/period";

const params = (q: string) => new URLSearchParams(q);
const TODAY = "2026-11-20";

describe("dashboard range from the address", () => {
  it("falls back to the last 30 days with day grouping and compare on", () => {
    expect(parseRange(params(""), TODAY)).toEqual({ from: "2026-10-22", to: "2026-11-20", group: "day", compare: true });
  });

  it("reads both dates, the grouping and compare=0", () => {
    expect(parseRange(params("from=2026-11-01&to=2026-11-15&groupBy=week&compare=0"), TODAY)).toEqual({ from: "2026-11-01", to: "2026-11-15", group: "week", compare: false });
  });

  it("ignores a half given or invalid range and an unknown grouping", () => {
    expect(parseRange(params("from=2026-11-01"), TODAY).from).toBe("2026-10-22");
    expect(parseRange(params("from=2026-02-31&to=2026-03-01"), TODAY).to).toBe("2026-11-20");
    expect(parseRange(params("groupBy=year"), TODAY).group).toBe("day");
  });

  it("picks a readable default grouping by length", () => {
    expect(defaultGroup("2026-11-20", "2026-11-20")).toBe("day");
    expect(defaultGroup("2026-10-01", "2026-12-31")).toBe("week");
    expect(defaultGroup("2026-01-01", "2026-11-20")).toBe("month");
    expect(parseRange(params("from=2026-01-01&to=2026-11-20"), TODAY).group).toBe("month");
  });

  it("writes only what differs from the defaults and round trips", () => {
    const base = { from: "2026-11-01", to: "2026-11-15", group: "day" as const, compare: true };
    expect(rangeToSearch(base)).toBe("from=2026-11-01&to=2026-11-15");
    const odd = { ...base, group: "month" as const, compare: false };
    expect(rangeToSearch(odd)).toBe("from=2026-11-01&to=2026-11-15&groupBy=month&compare=0");
    expect(parseRange(params(rangeToSearch(odd)), TODAY)).toEqual(odd);
  });

  it("applies the API limit: day grouping at most 366 days", () => {
    expect(periodProblem({ from: "2025-01-01", to: "2026-11-20", group: "day", compare: true })).toBe("tooLarge");
    expect(periodProblem({ from: "2025-01-01", to: "2026-11-20", group: "month", compare: true })).toBeNull();
    expect(periodProblem({ from: "2026-11-05", to: "2026-11-01", group: "day", compare: true })).toBe("invalid");
  });
});

describe("previous period and deltas", () => {
  it("is the same number of days directly before the range", () => {
    expect(previousRange("2026-11-20", "2026-11-20")).toEqual({ from: "2026-11-19", to: "2026-11-19" });
    expect(previousRange("2026-11-14", "2026-11-20")).toEqual({ from: "2026-11-07", to: "2026-11-13" });
    expect(previousRange("2026-11-01", "2026-11-20")).toEqual({ from: "2026-10-12", to: "2026-10-31" });
  });

  it("crosses a year end and a leap day", () => {
    expect(previousRange("2027-01-01", "2027-01-10")).toEqual({ from: "2026-12-22", to: "2026-12-31" });
    expect(previousRange("2028-03-01", "2028-03-03")).toEqual({ from: "2028-02-27", to: "2028-02-29" });
  });

  it("computes the difference and a percent, null when nothing to compare", () => {
    expect(delta(150, 100)).toEqual({ diff: 50, percent: 50 });
    expect(delta(50, 200)).toEqual({ diff: -150, percent: -75 });
    expect(delta(10, 0)).toEqual({ diff: 10, percent: null });
    expect(delta(100.456, 100)).toEqual({ diff: 0.46, percent: 0.5 });
  });

  it("puts the previous value on each point by position and leaves gaps", () => {
    const current = [{ label: "a", net: 1 }, { label: "b", net: 2 }, { label: "c", net: 3 }];
    expect(alignSeries(current, [{ net: 10 }, { net: 20 }]).map((p) => p.previous)).toEqual([10, 20, null]);
    expect(alignSeries(current, null).map((p) => p.previous)).toEqual([null, null, null]);
  });
});

describe("reshaping of report rows", () => {
  it("takes the best rows by a figure and keeps the API order on ties", () => {
    const rows = [{ n: "a", v: 1 }, { n: "b", v: 5 }, { n: "c", v: 5 }, { n: "d", v: 3 }];
    expect(topBy(rows, (r) => r.v, 3).map((r) => r.n)).toEqual(["b", "c", "d"]);
    expect(topBy([], (r: { v: number }) => r.v, 3)).toEqual([]);
  });

  it("splits sales by payment: COD against everything else, numbers from strings", () => {
    const out = splitByPayment([
      { key: "COD", orders: 2, total: "300.10" },
      { key: "bKash", orders: "1", total: 100.2 },
      { key: "Card", orders: 3, total: 50 },
    ]);
    expect(out.cod).toEqual({ orders: 2, total: 300.1 });
    expect(out.online).toEqual({ orders: 4, total: 150.2 });
  });

  it("counts out of stock among the low-stock rows and says when the list was cut", () => {
    expect(stockHealth(3, [{ available: 0 }, { available: 2 }, { available: -1 }])).toEqual({ low: 3, out: 2, exact: true });
    expect(stockHealth("250", [{ available: 0 }])).toEqual({ low: 250, out: 1, exact: false });
  });

  it("groups the dashboard's order statuses for the cards", () => {
    const c = statusCounts([
      { status: "HandedToCourier", count: 1 },
      { status: "InTransit", count: "2" },
      { status: "OutForDelivery", count: 3 },
      { status: "Delivered", count: 7 },
      { status: "Cancelled", count: 4 },
      { status: "Returned", count: 1 },
      { status: "ReturnInProgress", count: 2 },
      { status: "DeliveryFailed", count: 5 },
    ]);
    expect(c).toEqual({ inDelivery: 6, delivered: 7, cancelled: 4, returned: 3, failed: 5 });
    expect(statusCounts([])).toEqual({ inDelivery: 0, delivered: 0, cancelled: 0, returned: 0, failed: 0 });
  });
});

describe("links behind the cards", () => {
  it("uses only a known status and a complete valid day range", () => {
    expect(ordersLink()).toBe("/admin/orders");
    expect(ordersLink({ status: "Placed" })).toBe("/admin/orders?status=Placed");
    expect(ordersLink({ status: "Nonsense" })).toBe("/admin/orders");
    expect(ordersLink({ from: "2026-11-01", to: "2026-11-15" })).toBe("/admin/orders?from=2026-11-01&to=2026-11-15");
    expect(ordersLink({ status: "Delivered", from: "2026-11-01" })).toBe("/admin/orders?status=Delivered");
    expect(ordersLink({ from: "2026-13-01", to: "2026-11-15" })).toBe("/admin/orders");
  });
});
