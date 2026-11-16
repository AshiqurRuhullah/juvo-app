import { describe, expect, it } from "vitest";
import { addSubmitted, adjustmentProblem, balanceState, dayOfMonthProblem, expenseActions, expenseProblem, flowOf, netOf, parseSigned, receiptProblem, todayDhaka, transferProblem, wasSubmitted } from "@/modules/accounting/logic";

const can = { create: true, edit: true, delete: true, approve: true };

describe("expense workflow actions", () => {
  it("a draft can be edited, submitted and deleted", () => {
    expect(expenseActions("Draft", can, false)).toEqual({ edit: true, submit: true, delete: true, approve: false, reject: false, returnToDraft: false });
  });
  it("a submitted expense can be approved, rejected or taken back", () => {
    expect(expenseActions("Submitted", can, false)).toMatchObject({ approve: true, reject: true, returnToDraft: true, edit: false });
  });
  it("the author does not get the approve button but may still take it back", () => {
    expect(expenseActions("Submitted", can, true)).toMatchObject({ approve: false, reject: true, returnToDraft: true });
  });
  it("approved is final; rejected can be reopened", () => {
    expect(Object.values(expenseActions("Approved", can, false)).every((v) => v === false)).toBe(true);
    expect(expenseActions("Rejected", can, false)).toMatchObject({ returnToDraft: true, approve: false });
  });
  it("a view-only person gets nothing", () => {
    expect(Object.values(expenseActions("Submitted", { create: false, edit: false, delete: false, approve: false }, false)).every((v) => v === false)).toBe(true);
  });
});

describe("expense form", () => {
  const ok = { categoryId: "c", accountId: "a", amount: "250.50", date: "2026-11-01", method: "Cash", branchId: "b" };
  it("accepts a complete form", () => expect(expenseProblem(ok)).toBeNull());
  it("finds the first problem", () => {
    expect(expenseProblem({ ...ok, categoryId: "" })).toBe("category");
    expect(expenseProblem({ ...ok, accountId: "" })).toBe("account");
    expect(expenseProblem({ ...ok, amount: "0" })).toBe("amount");
    expect(expenseProblem({ ...ok, amount: "abc" })).toBe("amount");
    expect(expenseProblem({ ...ok, date: "" })).toBe("date");
  });
  it("checks receipts before upload", () => {
    expect(receiptProblem({ type: "image/png", size: 1000 })).toBeNull();
    expect(receiptProblem({ type: "application/pdf", size: 1000 })).toBeNull();
    expect(receiptProblem({ type: "text/plain", size: 10 })).toBe("type");
    expect(receiptProblem({ type: "image/png", size: 9 * 1024 * 1024 })).toBe("size");
  });
  it("allows day 1 to 28 for a repeating expense", () => {
    expect(dayOfMonthProblem("1")).toBe(false);
    expect(dayOfMonthProblem("28")).toBe(false);
    expect(dayOfMonthProblem("29")).toBe(true);
    expect(dayOfMonthProblem("0")).toBe(true);
  });
});

describe("balances and flows", () => {
  it("reads the direction of a signed amount", () => {
    expect(flowOf(10)).toBe("in");
    expect(flowOf(-0.01)).toBe("out");
    expect(flowOf(0)).toBe("zero");
  });
  it("names an overdrawn balance", () => {
    expect(balanceState(-5)).toBe("overdrawn");
    expect(balanceState(0)).toBe("zero");
    expect(balanceState(5)).toBe("positive");
  });
  it("sums a day book page to the cent", () => {
    expect(netOf([0.1, 0.2, -0.05])).toBe(0.25);
    expect(netOf([])).toBe(0);
  });
});

describe("transfers and adjustments", () => {
  it("needs two different accounts and an amount", () => {
    expect(transferProblem("", "b", "5")).toBe("from");
    expect(transferProblem("a", "", "5")).toBe("to");
    expect(transferProblem("a", "a", "5")).toBe("same");
    expect(transferProblem("a", "b", "0")).toBe("amount");
    expect(transferProblem("a", "b", "5")).toBeNull();
  });
  it("parses a signed adjustment", () => {
    expect(parseSigned("-120.5")).toBe(-120.5);
    expect(parseSigned("+৫০")).toBe(50);
    expect(parseSigned("x")).toBeNull();
    expect(adjustmentProblem("0", "r")).toBe("amount");
    expect(adjustmentProblem("-5", " ")).toBe("reason");
    expect(adjustmentProblem("-5", "count was short")).toBeNull();
  });
});

describe("remembering what this person submitted", () => {
  it("stores ids most recent first and finds them", () => {
    const one = addSubmitted(null, "a");
    const two = addSubmitted(one, "b");
    expect(JSON.parse(two)).toEqual(["b", "a"]);
    expect(wasSubmitted(two, "a")).toBe(true);
    expect(wasSubmitted(two, "z")).toBe(false);
    expect(wasSubmitted("not json", "a")).toBe(false);
    expect(JSON.parse(addSubmitted(two, "a"))).toEqual(["a", "b"]);
  });
  it("uses the Dhaka day", () => {
    expect(todayDhaka(new Date("2026-11-14T20:00:00Z"))).toBe("2026-11-15");
  });
});
