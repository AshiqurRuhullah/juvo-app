import { describe, expect, it } from "vitest";
import { availableActions, batchTotals, checkLine, lineDifference, lineErrors, num, pendingAmount, round2, selectionTotals, shipmentIdsToSend, toReconcileLines, type LineInput } from "@/modules/cod/logic";

// The worked example of docs/couriers-api.md section 8.
const example: LineInput[] = [
  { shipmentId: "a", expected: 1060, received: "1060.00", note: "" },
  { shipmentId: "b", expected: 1560, received: "1510", note: "kept 50 for change" },
  { shipmentId: "c", expected: 2060, received: "2080", note: "customer tipped 20" },
];

describe("difference", () => {
  it("is received minus expected, rounded to two decimals", () => {
    expect(lineDifference(1560, "1510")).toBe(-50);
    expect(lineDifference(2060, "2080.00")).toBe(20);
    expect(lineDifference(100.1, "100.3")).toBe(0.2);
    expect(lineDifference(1060, "1060")).toBe(0);
  });
  it("is null while the amount is invalid", () => {
    for (const bad of ["", "-1", "10.123", "abc", "1,5"]) expect(lineDifference(100, bad)).toBeNull();
  });
  it("rounds sums without float noise", () => {
    expect(round2(0.1 + 0.2)).toBe(0.3);
    expect(num("12.50")).toBe(12.5);
    expect(num(null)).toBe(0);
  });
});

describe("batch totals", () => {
  it("match the worked example", () => {
    const t = batchTotals(example);
    expect(t).toEqual({ expected: 4680, received: 4650, difference: -30, short: 1, over: 1, invalid: 0, noteMissing: 0 });
  });
  it("counts invalid lines and leaves them out of the received total", () => {
    const t = batchTotals([...example, { shipmentId: "d", expected: 10, received: "", note: "" }]);
    expect(t.invalid).toBe(1);
    expect(t.received).toBe(4650);
  });
  it("counts short or over parcels that have no note", () => {
    const t = batchTotals([{ shipmentId: "a", expected: 100, received: "90", note: "  " }]);
    expect(t.noteMissing).toBe(1);
  });
});

describe("line checks and errors", () => {
  it("needs a note for short and over parcels only", () => {
    expect(checkLine({ shipmentId: "a", expected: 100, received: "100", note: "" }).valid).toBe(true);
    expect(checkLine({ shipmentId: "a", expected: 100, received: "99.99", note: "" }).noteMissing).toBe(true);
    expect(checkLine({ shipmentId: "a", expected: 100, received: "101", note: "tip" }).valid).toBe(true);
  });
  it("limits the note to 300 characters", () => {
    expect(checkLine({ shipmentId: "a", expected: 100, received: "100", note: "x".repeat(301) }).noteTooLong).toBe(true);
  });
  it("names what is wrong per parcel", () => {
    const errors = lineErrors([
      { shipmentId: "a", expected: 100, received: "-5", note: "" },
      { shipmentId: "b", expected: 100, received: "50", note: "" },
      { shipmentId: "c", expected: 100, received: "100", note: "" },
    ]);
    expect(errors).toEqual({ a: "amount", b: "note" });
    expect(lineErrors(example)).toEqual({});
  });
  it("sends every line explicitly with numbers and trimmed notes", () => {
    expect(toReconcileLines(example)).toEqual([
      { shipmentId: "a", received: 1060, note: null },
      { shipmentId: "b", received: 1510, note: "kept 50 for change" },
      { shipmentId: "c", received: 2080, note: "customer tipped 20" },
    ]);
  });
});

describe("available actions", () => {
  it("offers reconcile and cancel while open, settle only once reconciled", () => {
    expect(availableActions("Open", true)).toEqual({ reconcile: true, settle: false, cancel: true });
    expect(availableActions("Reconciled", true)).toEqual({ reconcile: true, settle: true, cancel: true });
  });
  it("offers nothing for a finished batch or without the permission", () => {
    expect(availableActions("Settled", true)).toEqual({ reconcile: false, settle: false, cancel: false });
    expect(availableActions("Cancelled", true)).toEqual({ reconcile: false, settle: false, cancel: false });
    expect(availableActions("Reconciled", false)).toEqual({ reconcile: false, settle: false, cancel: false });
  });
});

describe("new batch selection", () => {
  const items = [
    { id: "a", amount: 1060 },
    { id: "b", amount: 1560.5 },
    { id: "c", amount: 2060 },
  ];
  it("counts everything by default", () => {
    expect(selectionTotals(items, new Set())).toEqual({ count: 3, total: 4680.5 });
    expect(shipmentIdsToSend(items, new Set())).toBeUndefined();
  });
  it("counts the ticked subset and sends its ids", () => {
    expect(selectionTotals(items, new Set(["b"]))).toEqual({ count: 2, total: 3120 });
    expect(shipmentIdsToSend(items, new Set(["b"]))).toEqual(["a", "c"]);
    expect(selectionTotals(items, new Set(["a", "b", "c"]))).toEqual({ count: 0, total: 0 });
  });
  it("takes the collected amount of a parcel when the courier reported one", () => {
    expect(pendingAmount({ codAmount: 1000, codCollectedAmount: 1010 })).toBe(1010);
    expect(pendingAmount({ codAmount: "1000.00", codCollectedAmount: null })).toBe(1000);
  });
});
