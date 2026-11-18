import { describe, expect, it } from "vitest";
import {
  allocateSummary,
  dhakaToday,
  draftFromSettings,
  draftToRequest,
  earliestShipDate,
  emptyPreorderDraft,
  formatShipDate,
  latestShipDate,
  orderStateNotice,
  preorderLabel,
  sameDraft,
  twoYearsAhead,
  validatePreorderDraft,
  waitingLineCount,
} from "@/modules/preorders/logic";

describe("pre-order label", () => {
  it("is a dated label when the line is a pre-order with a ship date", () => {
    expect(preorderLabel({ isPreorder: true, preorderExpectedShipDate: "2026-12-20" })).toEqual({ kind: "dated", date: "2026-12-20" });
  });
  it("is an undated label without a date", () => {
    expect(preorderLabel({ isPreorder: true, preorderExpectedShipDate: null })).toEqual({ kind: "undated" });
    expect(preorderLabel({ isPreorder: true, preorderExpectedShipDate: "  " })).toEqual({ kind: "undated" });
  });
  it("is nothing for a normal line", () => {
    expect(preorderLabel({ isPreorder: false, preorderExpectedShipDate: "2026-12-20" })).toBeNull();
    expect(preorderLabel(null)).toBeNull();
    expect(preorderLabel({})).toBeNull();
  });
});

describe("ship dates", () => {
  it("prints a calendar date in the page language without sliding to another day", () => {
    expect(formatShipDate("2026-12-20", "en")).toMatch(/20\s+Dec\s+2026/);
    expect(formatShipDate("2026-01-01", "en")).toMatch(/1\s+Jan\s+2026/);
    expect(formatShipDate("2026-12-20", "bn")).toMatch(/২০/);
    expect(formatShipDate("2026-12-20T00:00:00", "en")).toMatch(/20\s+Dec\s+2026/);
  });
  it("returns the text unchanged when it is not a date", () => {
    expect(formatShipDate("soon", "en")).toBe("soon");
  });
  it("finds the first and the last date among the pre-order lines only", () => {
    const lines = [
      { isPreorder: true, preorderExpectedShipDate: "2026-12-20" },
      { isPreorder: true, preorderExpectedShipDate: "2026-12-05" },
      { isPreorder: true, preorderExpectedShipDate: null },
      { isPreorder: false, preorderExpectedShipDate: "2026-11-01" },
    ];
    expect(earliestShipDate(lines)).toBe("2026-12-05");
    expect(latestShipDate(lines)).toBe("2026-12-20");
    expect(earliestShipDate([{ isPreorder: false }])).toBeNull();
    expect(latestShipDate([])).toBeNull();
  });
});

describe("order state notice", () => {
  it("explains Waiting and Ready, and says nothing otherwise", () => {
    expect(orderStateNotice("Waiting")).toBe("waiting");
    expect(orderStateNotice("Ready")).toBe("ready");
    expect(orderStateNotice("None")).toBeNull();
    expect(orderStateNotice(undefined)).toBeNull();
  });
});

describe("allocate result", () => {
  it("is 'none' when nothing was served", () => {
    expect(allocateSummary({ linesAllocated: 0, ordersReady: 0, readyOrderIds: [] })).toEqual({ kind: "none" });
  });
  it("reports lines and ready orders, also when numbers arrive as strings", () => {
    expect(allocateSummary({ linesAllocated: 3, ordersReady: 2, readyOrderIds: ["a", "b"] })).toEqual({ kind: "some", lines: 3, orders: 2 });
    expect(allocateSummary({ linesAllocated: "1", ordersReady: "0" })).toEqual({ kind: "some", lines: 1, orders: 0 });
  });
});

describe("queue helpers", () => {
  it("counts the lines that still wait", () => {
    expect(waitingLineCount([{ waiting: 2 }, { waiting: 0 }, { waiting: "1" }])).toBe(2);
    expect(waitingLineCount([])).toBe(0);
  });
});

describe("settings form", () => {
  it("builds a draft from the product (no inherit) and from a variant (null means inherit)", () => {
    expect(draftFromSettings({ allowPreorder: true, expectedShipDate: "2026-12-20", advancePercent: 50 }, false)).toEqual({ allow: "yes", date: "2026-12-20", percent: "50" });
    expect(draftFromSettings({ allowPreorder: false, expectedShipDate: null, advancePercent: null }, false)).toEqual({ allow: "no", date: "", percent: "" });
    expect(draftFromSettings({ allowPreorder: null, expectedShipDate: null, advancePercent: null }, true).allow).toBe("inherit");
    expect(draftFromSettings({ allowPreorder: null, expectedShipDate: null, advancePercent: null }, false).allow).toBe("no");
  });

  it("accepts an empty or complete form", () => {
    expect(validatePreorderDraft(emptyPreorderDraft(), "2026-11-18")).toEqual({});
    expect(validatePreorderDraft({ allow: "yes", date: "2026-11-18", percent: "0" }, "2026-11-18")).toEqual({});
    expect(validatePreorderDraft({ allow: "yes", date: "2026-12-20", percent: "100" }, "2026-11-18")).toEqual({});
  });

  it("refuses a past date, a bad date, and an advance outside 0 to 100", () => {
    expect(validatePreorderDraft({ allow: "yes", date: "2026-11-17", percent: "" }, "2026-11-18")).toEqual({ date: "past" });
    expect(validatePreorderDraft({ allow: "yes", date: "2026-13-40", percent: "" }, "2026-11-18")).toEqual({ date: "invalid" });
    expect(validatePreorderDraft({ allow: "yes", date: "20/12/2026", percent: "" }, "2026-11-18")).toEqual({ date: "invalid" });
    expect(validatePreorderDraft({ allow: "yes", date: "", percent: "101" }, "2026-11-18")).toEqual({ percent: "range" });
    expect(validatePreorderDraft({ allow: "yes", date: "", percent: "-1" }, "2026-11-18")).toEqual({ percent: "range" });
    expect(validatePreorderDraft({ allow: "yes", date: "", percent: "abc" }, "2026-11-18")).toEqual({ percent: "invalid" });
  });

  it("refuses a date more than two years ahead (the API's limit)", () => {
    expect(twoYearsAhead("2026-11-18")).toBe("2028-11-18");
    expect(validatePreorderDraft({ allow: "yes", date: "2028-11-18", percent: "" }, "2026-11-18")).toEqual({});
    expect(validatePreorderDraft({ allow: "yes", date: "2028-11-19", percent: "" }, "2026-11-18")).toEqual({ date: "far" });
    expect(validatePreorderDraft({ allow: "yes", date: "2031-03-05", percent: "" }, "2026-11-18")).toEqual({ date: "far" });
  });

  it("builds the request: inherit is null, blanks clear the date and the advance", () => {
    expect(draftToRequest({ allow: "inherit", date: "", percent: "" })).toEqual({ allowPreorder: null, expectedShipDate: null, advancePercent: null });
    expect(draftToRequest({ allow: "yes", date: "2026-12-20", percent: "37.5" })).toEqual({ allowPreorder: true, expectedShipDate: "2026-12-20", advancePercent: 37.5 });
    expect(draftToRequest({ allow: "no", date: "", percent: "0" })).toEqual({ allowPreorder: false, expectedShipDate: null, advancePercent: 0 });
  });

  it("compares drafts", () => {
    expect(sameDraft({ allow: "yes", date: "", percent: "" }, { allow: "yes", date: "", percent: "" })).toBe(true);
    expect(sameDraft({ allow: "yes", date: "", percent: "" }, { allow: "no", date: "", percent: "" })).toBe(false);
  });

  it("reads today in Dhaka (UTC+6), which is already tomorrow late in the UTC evening", () => {
    expect(dhakaToday(Date.parse("2026-11-18T17:59:00Z"))).toBe("2026-11-18");
    expect(dhakaToday(Date.parse("2026-11-18T18:00:00Z"))).toBe("2026-11-19");
  });
});
