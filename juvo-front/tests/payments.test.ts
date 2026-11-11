import { describe, expect, it } from "vitest";
import { actionSet, countdown, isOnlineMethod, loadPayState, needsPayment, outcomeOf, payPath, proofProblem, returnPath, sandboxPath, savePayState, secondsLeft, withParam } from "@/modules/payments/status";

describe("payment methods", () => {
  it("COD and MANUAL are not gateway methods, everything else is", () => {
    expect(isOnlineMethod("COD")).toBe(false);
    expect(isOnlineMethod("MANUAL")).toBe(false);
    expect(isOnlineMethod("SANDBOX")).toBe(true);
    expect(isOnlineMethod("BKASH")).toBe(true);
  });

  it("an order needs paying while an online or manual payment is open, never for COD or a closed order", () => {
    expect(needsPayment("SANDBOX", "Pending", "Placed")).toBe(true);
    expect(needsPayment("SANDBOX", "Failed", "Placed")).toBe(true);
    expect(needsPayment("SANDBOX", "Cancelled", "Placed")).toBe(true);
    expect(needsPayment("MANUAL", "Pending", "Placed")).toBe(true);
    expect(needsPayment("MANUAL", "Failed", "Placed")).toBe(false);
    expect(needsPayment("SANDBOX", "Paid", "Confirmed")).toBe(false);
    expect(needsPayment("SANDBOX", "UnderReview", "Placed")).toBe(false);
    expect(needsPayment("COD", "Pending", "Placed")).toBe(false);
    expect(needsPayment("SANDBOX", "Pending", "Cancelled")).toBe(false);
  });
});

describe("return page outcome", () => {
  it("maps what the server says, and nothing else", () => {
    expect(outcomeOf(undefined)).toBe("pending");
    expect(outcomeOf({ status: "Pending", orderPaymentStatus: "Pending" })).toBe("pending");
    expect(outcomeOf({ status: "Paid", orderPaymentStatus: "Paid" })).toBe("paid");
    expect(outcomeOf({ status: "Refunded", orderPaymentStatus: "Refunded" })).toBe("paid");
    expect(outcomeOf({ status: "Failed", orderPaymentStatus: "Failed" })).toBe("failed");
    expect(outcomeOf({ status: "Rejected", orderPaymentStatus: "Rejected" })).toBe("failed");
    expect(outcomeOf({ status: "Cancelled", orderPaymentStatus: "Pending" })).toBe("cancelled");
    expect(outcomeOf({ status: "UnderReview", orderPaymentStatus: "UnderReview" })).toBe("review");
  });
});

describe("hold timer", () => {
  const deadline = "2026-11-11T10:30:00.000Z";
  const at = (iso: string) => new Date(iso).getTime();

  it("counts down to the deadline and never goes below zero", () => {
    expect(secondsLeft(deadline, at("2026-11-11T10:00:00.000Z"))).toBe(1800);
    expect(secondsLeft(deadline, at("2026-11-11T10:29:59.500Z"))).toBe(0);
    expect(secondsLeft(deadline, at("2026-11-11T11:00:00.000Z"))).toBe(0);
  });

  it("has no timer without a usable deadline", () => {
    expect(secondsLeft(null, 0)).toBeNull();
    expect(secondsLeft("not a date", 0)).toBeNull();
  });

  it("writes minutes and seconds, hours when needed", () => {
    expect(countdown(1745)).toBe("29:05");
    expect(countdown(59)).toBe("0:59");
    expect(countdown(3723)).toBe("1:02:03");
  });
});

describe("addresses between the pay pages", () => {
  it("builds the paths", () => {
    expect(returnPath("JV-HO-2611-000123")).toBe("/pay/return?order=JV-HO-2611-000123");
    expect(payPath("JV-1")).toBe("/pay/JV-1");
    expect(payPath("JV-1", "a b", true)).toBe("/pay/JV-1?token=a%20b&retry=1");
    expect(sandboxPath("sbx_1", "p-1")).toBe("/pay/sandbox/sbx_1?payment=p-1");
  });

  it("adds a query value to a same-site path and refuses anything off-site", () => {
    expect(withParam("/pay/return?order=JV-1", "payment", "p1", "/x")).toBe("/pay/return?order=JV-1&payment=p1");
    expect(withParam("/pay/return", "payment", "p1", "/x")).toBe("/pay/return?payment=p1");
    expect(withParam("/pay/return?payment=old", "payment", "new", "/x")).toBe("/pay/return?payment=new");
    expect(withParam("https://evil.example/pay", "payment", "p1", "/x")).toBe("/x");
    expect(withParam("//evil.example/pay", "payment", "p1", "/x")).toBe("/x");
    expect(withParam(null, "payment", "p1", "/x")).toBe("/x");
  });
});

describe("pay state kept in the tab", () => {
  const memory = () => {
    const data = new Map<string, string>();
    return { getItem: (k: string) => data.get(k) ?? null, setItem: (k: string, v: string) => void data.set(k, v) };
  };

  it("round-trips and ignores junk", () => {
    const storage = memory();
    expect(loadPayState(storage, "JV-1")).toBeNull();
    savePayState(storage, "JV-1", { paymentId: "p1", token: "t", deadline: null });
    expect(loadPayState(storage, "JV-1")).toEqual({ paymentId: "p1", token: "t", deadline: null });
    storage.setItem("juvo_pay_JV-2", "{not json");
    expect(loadPayState(storage, "JV-2")).toBeNull();
    storage.setItem("juvo_pay_JV-3", JSON.stringify({ token: "t" }));
    expect(loadPayState(storage, "JV-3")).toBeNull();
    expect(loadPayState(null, "JV-1")).toBeNull();
  });
});

describe("proof file", () => {
  it("accepts JPEG, PNG, WebP and PDF up to 5 MB", () => {
    expect(proofProblem({ type: "image/png", size: 1000 })).toBeNull();
    expect(proofProblem({ type: "application/pdf", size: 5 * 1024 * 1024 })).toBeNull();
    expect(proofProblem({ type: "image/gif", size: 1000 })).toBe("type");
    expect(proofProblem({ type: "image/jpeg", size: 5 * 1024 * 1024 + 1 })).toBe("size");
  });
});

describe("staff actions", () => {
  it("reads the server's next actions in any case", () => {
    const set = actionSet(["approve", "Reject", "REFUND"]);
    expect(set.has("approve")).toBe(true);
    expect(set.has("reject")).toBe(true);
    expect(set.has("refund")).toBe(true);
    expect(set.has("settle")).toBe(false);
  });
});
