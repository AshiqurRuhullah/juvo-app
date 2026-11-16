import { describe, expect, it } from "vitest";
import { allowedActions, draftProblem, draftTotal, dueState, lineTotal, parseAmount, parseQuantity, paymentProblem, receiptCostPending, receiveLines, receiveProblems, round2, suggestionToLine } from "@/modules/purchasing/logic";

const all = { edit: true, send: true, receive: true, cancel: true, pay: true };
const none = { edit: false, send: false, receive: false, cancel: false, pay: false };

describe("money maths", () => {
  it("rounds half away from zero without float drift", () => {
    expect(round2(0.1 + 0.2)).toBe(0.3);
    expect(round2(1.005)).toBe(1.01);
    expect(round2(-1.005)).toBe(-1.01);
  });
  it("parses amounts in either language", () => {
    expect(parseAmount("1,250.5")).toBe(1250.5);
    expect(parseAmount("১২৫০")).toBe(1250);
    expect(parseAmount("12.345")).toBeNull();
    expect(parseAmount("-1")).toBeNull();
    expect(parseAmount("")).toBeNull();
    expect(parseQuantity("১২")).toBe(12);
    expect(parseQuantity("1.5")).toBeNull();
  });
  it("totals lines and ignores half-typed ones", () => {
    expect(lineTotal(3, 12.35)).toBe(37.05);
    expect(draftTotal([{ quantity: "2", unitCost: "10.5" }, { quantity: "3", unitCost: "" }, { quantity: "1", unitCost: "0.25" }])).toBe(21.25);
  });
});

describe("draft validation", () => {
  const ok = { supplierId: "s", branchId: "b", lines: [{ quantity: "1", unitCost: "5" }] };
  it("accepts a complete draft", () => expect(draftProblem(ok)).toBeNull());
  it("names the first problem", () => {
    expect(draftProblem({ ...ok, supplierId: "" })).toBe("supplier");
    expect(draftProblem({ ...ok, branchId: "" })).toBe("branch");
    expect(draftProblem({ ...ok, lines: [] })).toBe("noLines");
    expect(draftProblem({ ...ok, lines: [{ quantity: "0", unitCost: "5" }] })).toBe("quantity");
    expect(draftProblem({ ...ok, lines: [{ quantity: "2", unitCost: "x" }] })).toBe("cost");
  });
});

describe("allowed actions per status", () => {
  const facts = (status: string, amountPaid = 0, outstanding = 100) => ({ status, amountPaid, outstanding });
  it("a draft can be edited, sent and cancelled but not received or paid", () => {
    const a = allowedActions(facts("Draft"), all);
    expect([a.edit, a.send, a.cancel]).toEqual([true, true, true]);
    expect([a.receive, a.pay, a.invoice, a.closeShort]).toEqual([false, false, false, false]);
  });
  it("a sent order can be received, invoiced, paid and cancelled until money is paid", () => {
    expect(allowedActions(facts("Sent"), all)).toMatchObject({ receive: true, invoice: true, pay: true, cancel: true, edit: false, closeShort: false });
    expect(allowedActions(facts("Sent", 10), all).cancel).toBe(false);
  });
  it("only a partly received order can be closed short", () => {
    expect(allowedActions(facts("PartlyReceived"), all).closeShort).toBe(true);
    expect(allowedActions(facts("Received"), all)).toMatchObject({ receive: false, closeShort: false, cancel: false });
  });
  it("nothing to pay when nothing is due; cancelled offers nothing", () => {
    expect(allowedActions(facts("Received", 100, 0), all).pay).toBe(false);
    expect(allowedActions(facts("Cancelled"), all)).toMatchObject({ edit: false, send: false, receive: false, pay: false, invoice: false, cancel: false });
  });
  it("permissions switch actions off", () => {
    const a = allowedActions(facts("Sent"), none);
    expect(Object.values(a).every((v) => v === false)).toBe(true);
    expect(allowedActions(facts("Sent"), { ...all, receive: false }).receive).toBe(false);
  });
});

describe("receiving", () => {
  const rows = [
    { lineId: "a", outstanding: 5, accepted: "3", rejected: "1" },
    { lineId: "b", outstanding: 2, accepted: "", rejected: "" },
  ];
  it("accepts a partial receipt and sends only the lines with units", () => {
    expect(receiveProblems(rows)).toEqual([]);
    expect(receiveLines(rows)).toEqual([{ lineId: "a", acceptedQuantity: 3, rejectedQuantity: 1, note: null }]);
  });
  it("refuses more accepted units than still expected (damaged units do not count)", () => {
    expect(receiveProblems([{ lineId: "a", outstanding: 2, accepted: "3", rejected: "" }])).toEqual([{ lineId: "a", kind: "over" }]);
    expect(receiveProblems([{ lineId: "a", outstanding: 2, accepted: "2", rejected: "9" }])).toEqual([]);
  });
  it("needs at least one unit and valid numbers", () => {
    expect(receiveProblems([{ lineId: "a", outstanding: 2, accepted: "", rejected: "" }])).toEqual([{ lineId: null, kind: "nothing" }]);
    expect(receiveProblems([{ lineId: "a", outstanding: 2, accepted: "x", rejected: "" }])).toEqual([{ lineId: "a", kind: "invalid" }]);
  });
});

describe("supplier payments", () => {
  it("must be positive and not above what is due", () => {
    expect(paymentProblem("", 100)).toBe("amount");
    expect(paymentProblem("0", 100)).toBe("amount");
    expect(paymentProblem("100.01", 100)).toBe("exceeds");
    expect(paymentProblem("100", 100)).toBeNull();
    expect(paymentProblem("৫০", 100)).toBeNull();
  });
  it("shows a settled balance as settled", () => {
    expect(dueState(0)).toBe("settled");
    expect(dueState(0.01)).toBe("due");
  });
});

describe("suggestions and receipts", () => {
  it("turns a suggestion into a line", () => {
    expect(suggestionToLine({ suggestedQuantity: 12, lastUnitCost: "450.5" })).toEqual({ quantity: "12", unitCost: "450.5" });
    expect(suggestionToLine({ suggestedQuantity: 0, lastUnitCost: null })).toEqual({ quantity: "1", unitCost: "" });
  });
  it("tells when a receipt still waits for its cost to be applied", () => {
    expect(receiptCostPending([{ acceptedQuantity: 2, costApplied: false }])).toBe(true);
    expect(receiptCostPending([{ acceptedQuantity: 0, costApplied: false }, { acceptedQuantity: 2, costApplied: true }])).toBe(false);
  });
});
