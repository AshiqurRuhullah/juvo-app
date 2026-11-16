import { describe, expect, it } from "vitest";
import {
  ACCOUNTANT_KINDS,
  GROUP_BY,
  SECTIONS,
  accountantUrl,
  addDays,
  allowedFormats,
  apiQuery,
  bangladeshToday,
  costKind,
  detectPreset,
  durationParts,
  effectiveGroupBy,
  exportPermission,
  exportUrl,
  filenameFrom,
  filtersToSearch,
  isIsoDate,
  isReportName,
  kindUsesRange,
  num,
  numOrNull,
  openableReports,
  parseFilters,
  percentText,
  presetRange,
  rangeLimit,
  rangeProblem,
  shapeCourier,
  shapeFunnel,
  shapeSales,
  shapeSeries,
  shapeStock,
  spanDays,
  sum,
  viewPermission,
} from "@/modules/reports/logic";
import { buildKpis, changeFrom } from "@/modules/dashboard/logic";

const TODAY = "2026-11-16";
const params = (q: string) => new URLSearchParams(q);

describe("Bangladesh days", () => {
  it("is the next day in Dhaka after 18:00 UTC", () => {
    expect(bangladeshToday(new Date("2026-11-15T17:59:00Z"))).toBe("2026-11-15");
    expect(bangladeshToday(new Date("2026-11-15T18:00:00Z"))).toBe("2026-11-16");
    expect(bangladeshToday(new Date("2026-12-31T20:00:00Z"))).toBe("2027-01-01");
  });
  it("adds days across month and year ends and leap days", () => {
    expect(addDays("2026-11-30", 1)).toBe("2026-12-01");
    expect(addDays("2026-01-01", -1)).toBe("2025-12-31");
    expect(addDays("2028-02-28", 1)).toBe("2028-02-29");
  });
  it("counts both ends of a range", () => {
    expect(spanDays("2026-11-16", "2026-11-16")).toBe(1);
    expect(spanDays("2026-11-01", "2026-11-30")).toBe(30);
  });
  it("checks real calendar dates", () => {
    expect(isIsoDate("2026-02-29")).toBe(false);
    expect(isIsoDate("2028-02-29")).toBe(true);
    expect(isIsoDate("2026-13-01")).toBe(false);
    expect(isIsoDate("16/11/2026")).toBe(false);
    expect(isIsoDate(undefined)).toBe(false);
  });
});

describe("date presets", () => {
  it("builds every preset from today", () => {
    expect(presetRange("today", TODAY)).toEqual({ from: TODAY, to: TODAY });
    expect(presetRange("last7", TODAY)).toEqual({ from: "2026-11-10", to: TODAY });
    expect(presetRange("last30", TODAY)).toEqual({ from: "2026-10-18", to: TODAY });
    expect(presetRange("thisMonth", TODAY)).toEqual({ from: "2026-11-01", to: TODAY });
    expect(presetRange("lastMonth", TODAY)).toEqual({ from: "2026-10-01", to: "2026-10-31" });
  });
  it("last month works in January and in a short February", () => {
    expect(presetRange("lastMonth", "2027-01-05")).toEqual({ from: "2026-12-01", to: "2026-12-31" });
    expect(presetRange("lastMonth", "2027-03-02")).toEqual({ from: "2027-02-01", to: "2027-02-28" });
  });
  it("last 7 days spans exactly 7 days and last 30 exactly 30", () => {
    const a = presetRange("last7", TODAY);
    const b = presetRange("last30", TODAY);
    expect(spanDays(a.from, a.to)).toBe(7);
    expect(spanDays(b.from, b.to)).toBe(30);
  });
  it("recognises a preset or says custom", () => {
    expect(detectPreset("2026-11-10", TODAY, TODAY)).toBe("last7");
    expect(detectPreset("2026-10-01", "2026-10-31", TODAY)).toBe("lastMonth");
    expect(detectPreset("2026-11-02", "2026-11-05", TODAY)).toBe("custom");
  });
});

describe("range checks (the API's limits)", () => {
  it("accepts a normal range", () => expect(rangeProblem("2026-11-01", "2026-11-16")).toBeNull());
  it("asks for both dates", () => {
    expect(rangeProblem(undefined, "2026-11-16")).toBe("missing");
    expect(rangeProblem("2026-11-01", "")).toBe("missing");
    expect(rangeProblem("2026-02-30", "2026-03-01")).toBe("missing");
  });
  it("refuses an end before the start", () => expect(rangeProblem("2026-11-16", "2026-11-01")).toBe("invalid"));
  it("limits daily grouping to 366 days and the rest to 1830", () => {
    expect(rangeProblem("2025-11-16", "2026-11-16", "day")).toBeNull(); // 366 days
    expect(rangeProblem("2025-11-15", "2026-11-16", "day")).toBe("tooLarge"); // 367 days
    expect(rangeProblem("2025-11-15", "2026-11-16", "month")).toBeNull();
    expect(rangeProblem("2021-01-01", "2026-11-16", "month")).toBe("tooLarge");
    expect(rangeLimit("day")).toBe(366);
    expect(rangeLimit("week")).toBe(1830);
  });
  it("accepts an explicit maximum for the accountant export", () => {
    expect(rangeProblem("2025-11-15", "2026-11-16", "day", 366)).toBe("tooLarge");
    expect(rangeLimit("day", 366)).toBe(366);
  });
});

describe("filters in the address", () => {
  it("falls back to the last 30 days when the address has nothing", () => {
    expect(parseFilters(params(""), "sales", TODAY)).toEqual({ from: "2026-10-18", to: TODAY, branchId: undefined, groupBy: undefined, days: undefined });
  });
  it("round-trips every filter", () => {
    const branch = "0b0e5a52-1c7e-4a35-9f31-0a1d6c2f7e11";
    const filters = { from: "2026-10-01", to: "2026-10-31", branchId: branch, groupBy: "product", days: 90 };
    expect(parseFilters(params(filtersToSearch(filters)), "sales", TODAY)).toEqual(filters);
  });
  it("keeps an explicit consolidated choice instead of the default branch", () => {
    const search = filtersToSearch({ from: "2026-10-01", to: "2026-10-31" });
    expect(parseFilters(params(search), "plain", TODAY, "default-branch").branchId).toBeUndefined();
    expect(parseFilters(params("from=2026-10-01&to=2026-10-31"), "plain", TODAY, "0b0e5a52-1c7e-4a35-9f31-0a1d6c2f7e11").branchId).toBe("0b0e5a52-1c7e-4a35-9f31-0a1d6c2f7e11");
  });
  it("drops what is wrong: bad dates, a groupBy the report does not offer, a branch that is not an id, bad days", () => {
    const f = parseFilters(params("from=2026-02-30&to=x&groupBy=zone&branch=nope&days=-3"), "finance", TODAY);
    expect(f.from).toBe("2026-10-18");
    expect(f.groupBy).toBeUndefined();
    expect(f.branchId).toBeUndefined();
    expect(f.days).toBeUndefined();
    expect(parseFilters(params("groupBy=zone"), "sales", TODAY).groupBy).toBe("zone");
    expect(parseFilters(params("days=4000"), "plain", TODAY).days).toBeUndefined();
  });
  it("uses the day grouping for the range limit when none is chosen", () => {
    expect(effectiveGroupBy({ from: "a", to: "b" })).toBe("day");
    expect(effectiveGroupBy({ from: "a", to: "b", groupBy: "month" })).toBe("month");
  });
  it("sends only what is set to the API", () => {
    expect(apiQuery({ from: "2026-10-01", to: "2026-10-31", branchId: undefined, groupBy: "week" }, { limit: 100, section: "" })).toEqual({ from: "2026-10-01", to: "2026-10-31", groupBy: "week", limit: 100 });
  });
  it("offers the groupings of the contract", () => {
    expect(GROUP_BY.sales).toHaveLength(10);
    expect(GROUP_BY.finance).toEqual(["day", "week", "month"]);
    expect(GROUP_BY.pnl).toEqual(["day", "week", "month"]);
  });
});

describe("numbers", () => {
  it("reads numbers and numeric strings, never NaN", () => {
    expect(num("12.50")).toBe(12.5);
    expect(num(null)).toBe(0);
    expect(num("abc")).toBe(0);
    expect(numOrNull(null)).toBeNull();
    expect(numOrNull("3")).toBe(3);
  });
  it("shows percentages the API already computed", () => {
    expect(percentText(12.5)).toBe("12.5%");
    expect(percentText("100")).toBe("100%");
    expect(percentText(33.333)).toBe("33.3%");
    expect(percentText(12.5, "bn")).toContain("%");
  });
  it("splits hours into days, hours and minutes", () => {
    expect(durationParts(null)).toBeNull();
    expect(durationParts(0.75)).toEqual({ days: 0, hours: 0, minutes: 45 });
    expect(durationParts(3.5)).toEqual({ days: 0, hours: 3, minutes: 30 });
    expect(durationParts(52)).toEqual({ days: 2, hours: 4, minutes: 0 });
  });
  it("sums money to the cent", () => expect(sum([0.1, "0.2", null, 0.3])).toBe(0.6));
});

describe("shaping for tables and charts", () => {
  const stages = [
    { stage: "Placed", count: 10, rateOfPlaced: 100, rateOfPrevious: 100 },
    { stage: "Confirmed", count: 8, rateOfPlaced: 80, rateOfPrevious: 80 },
    { stage: "Delivered", count: "5", rateOfPlaced: "50", rateOfPrevious: "62.5" },
    { stage: "Cancelled", count: 2, rateOfPlaced: 20, rateOfPrevious: 20 },
    { stage: "Returned", count: 1, rateOfPlaced: 10, rateOfPrevious: 10 },
  ];
  it("separates the funnel from the losses and sizes the bars against the placed orders", () => {
    const f = shapeFunnel(stages);
    expect(f.main.map((s) => s.stage)).toEqual(["Placed", "Confirmed", "Delivered"]);
    expect(f.main.map((s) => s.width)).toEqual([100, 80, 50]);
    expect(f.losses.map((s) => s.stage)).toEqual(["Cancelled", "Returned"]);
    expect(f.main[2]!.count).toBe(5);
  });
  it("does not divide by zero when nothing was placed", () => {
    expect(shapeFunnel([{ stage: "Placed", count: 0, rateOfPlaced: 0, rateOfPrevious: 0 }]).main[0]!.width).toBe(0);
  });
  it("keeps hidden cost as null in a series (a gap, never a zero)", () => {
    const points = shapeSeries([{ label: "1 Nov", netRevenue: "100", deliveryCollected: 20, grossProfit: null, netProfit: null, orders: 2, expenses: "5" }]);
    expect(points[0]).toEqual({ label: "1 Nov", revenue: 100, delivery: 20, profit: null, net: null, orders: 2, expenses: 5 });
    expect(shapeSeries([{ label: "x", netRevenue: 1, deliveryCollected: 0, orders: 1 }])[0]!.profit).toBeNull();
  });
  const rows = [
    { key: "a", label: "A", orders: 1, units: 1, net: 100, total: 100 },
    { key: "b", label: "B", orders: 2, units: 3, net: "300", total: 300 },
    { key: "c", label: "C", orders: 1, units: 1, net: 200, total: 200 },
  ];
  it("keeps time order for time groupings and sorts the others by net sales, at most `max`", () => {
    expect(shapeSales(rows, "day").map((r) => r.label)).toEqual(["A", "B", "C"]);
    expect(shapeSales(rows, "product").map((r) => r.label)).toEqual(["B", "C", "A"]);
    expect(shapeSales(rows, "product", 2)).toHaveLength(2);
  });
  it("shapes courier rates and stock value", () => {
    expect(shapeCourier([{ courier: "Pathao", deliveredRate: "80", failedRate: 15, returnedRate: 5 }])).toEqual([{ label: "Pathao", delivered: 80, failed: 15, returned: 5 }]);
    expect(shapeStock([{ branchCode: "HO", valueAtPrice: "1000", valueAtCost: null }])).toEqual([{ label: "HO", price: 1000, cost: null }]);
  });
});

describe("export addresses and file names", () => {
  const filters = { from: "2026-10-01", to: "2026-10-31", branchId: undefined, groupBy: "month" };
  it("builds a CSV address with the section", () => {
    const url = exportUrl("inventory", { ...filters, days: 90 }, { format: "csv", section: "dead", lang: "bn" });
    expect(url.startsWith("/api/bff/api/v1/reports/inventory/export?")).toBe(true);
    const q = new URL(url, "http://x").searchParams;
    expect(q.get("format")).toBe("csv");
    expect(q.get("section")).toBe("dead");
    expect(q.get("lang")).toBeNull(); // language only applies to the PDF
    expect(q.get("days")).toBe("90");
    expect(q.get("branchId")).toBeNull();
  });
  it("builds a PDF address with the language and no section", () => {
    const q = new URL(exportUrl("sales", filters, { format: "pdf", lang: "bn", section: "x" }), "http://x").searchParams;
    expect(q.get("lang")).toBe("bn");
    expect(q.get("section")).toBeNull();
    expect(q.get("groupBy")).toBe("month");
    expect(new URL(exportUrl("sales", filters, { format: "pdf" }), "http://x").searchParams.get("lang")).toBe("en");
  });
  it("builds the accountant address and leaves the range out for payables", () => {
    const daybook = new URL(accountantUrl("daybook", "xlsx", { ...filters, branchId: "b1" }), "http://x");
    expect(daybook.pathname).toBe("/api/bff/api/v1/accounting/export");
    expect(daybook.searchParams.get("kind")).toBe("daybook");
    expect(daybook.searchParams.get("format")).toBe("xlsx");
    expect(daybook.searchParams.get("from")).toBe("2026-10-01");
    expect(daybook.searchParams.get("branchId")).toBe("b1");
    expect(kindUsesRange("payables")).toBe(false);
    expect(ACCOUNTANT_KINDS).toHaveLength(5);
    expect(costKind("pnl")).toBe(true);
    expect(costKind("purchases")).toBe(true);
    expect(costKind("expenses")).toBe(false);
  });
  it("lists the sections of the contract", () => {
    expect(SECTIONS.orders).toEqual(["funnel", "cancellations", "returns"]);
    expect(SECTIONS.inventory).toEqual(["summary", "low", "movements", "dead"]);
    expect(SECTIONS.finance).toEqual(["finance", "cod"]);
    expect(SECTIONS.customers).toEqual(["summary", "top", "conversion"]);
    expect(SECTIONS.sales).toBeUndefined();
  });
  it("takes the file name the server chose", () => {
    expect(filenameFrom('attachment; filename="sales-2026-10-01_2026-10-31.csv"', "x.csv")).toBe("sales-2026-10-01_2026-10-31.csv");
    expect(filenameFrom("attachment; filename=report.pdf; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", "x.pdf")).toBe("résumé.pdf");
    expect(filenameFrom(null, "fallback.csv")).toBe("fallback.csv");
    expect(filenameFrom("attachment", "fallback.csv")).toBe("fallback.csv");
  });
  it("never keeps a path or odd characters in a file name", () => {
    expect(filenameFrom('attachment; filename="../../etc/passwd"', "x")).toBe("passwd");
    expect(filenameFrom('attachment; filename="a:b*c.csv"', "x")).toBe("a_b_c.csv");
  });
});

describe("what a person may export and open", () => {
  it("offers the export formats only with the export permission", () => {
    expect(allowedFormats(true)).toEqual(["csv", "xlsx", "pdf"]);
    expect(allowedFormats(false)).toEqual([]);
  });
  it("names the permissions of a report", () => {
    expect(viewPermission("sales")).toBe("reports.sales.view");
    expect(exportPermission("customers")).toBe("reports.customers.export");
    expect(isReportName("courier")).toBe(true);
    expect(isReportName("pnl")).toBe(false);
  });
  it("lists the reports a person may open, in menu order", () => {
    const held = new Set(["reports.finance.view", "reports.sales.view", "reports.customers.export"]);
    expect(openableReports((code) => held.has(code))).toEqual(["sales", "finance"]);
    expect(openableReports(() => false)).toEqual([]);
  });
});

describe("dashboard cards", () => {
  it("compares today with yesterday and says nothing when yesterday was empty", () => {
    expect(changeFrom(150, 100)).toBe(50);
    expect(changeFrom(50, 100)).toBe(-50);
    expect(changeFrom(10, 0)).toBeNull();
  });
  it("builds the four cards from the real series", () => {
    const series = [
      { sales: 0, orders: 0 },
      { sales: "100", orders: 1 },
      { sales: "300", orders: 2 },
    ];
    const kpis = buildKpis(series, { sales: "300", orders: 2 }, 4);
    expect(kpis.map((k) => k.key)).toEqual(["sales", "orders", "average", "toConfirm"]);
    expect(kpis[0]).toMatchObject({ value: 300, change: 200, spark: [0, 100, 300] });
    expect(kpis[1]).toMatchObject({ value: 2, change: 100 });
    expect(kpis[2]).toMatchObject({ value: 150, change: 50 });
    expect(kpis[3]).toMatchObject({ value: 4, change: null, spark: [] });
  });
  it("a quiet day has zero values and no change instead of invented numbers", () => {
    const kpis = buildKpis([{ sales: 0, orders: 0 }, { sales: 0, orders: 0 }], { sales: 0, orders: 0 }, 0);
    expect(kpis.map((k) => k.value)).toEqual([0, 0, 0, 0]);
    expect(kpis.map((k) => k.change)).toEqual([null, null, null, null]);
  });
});
