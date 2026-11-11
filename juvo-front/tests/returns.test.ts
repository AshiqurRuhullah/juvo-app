import { describe, expect, it } from "vitest";
import {
  ACTION_PERMISSION,
  allowedActions,
  buildLines,
  clampQuantity,
  customerStepIndex,
  defaultFault,
  defaultInspect,
  digitsOnly,
  estimateRefund,
  isClosed,
  legalActions,
  maxQuantity,
  photoProblem,
  photoSizeProblem,
  pickupBounds,
  refundRows,
  RETURN_STATUSES,
  statusTone,
  withCondition,
} from "@/modules/returns/logic";

describe("status mapping", () => {
  it("knows the five final states", () => {
    const closed = RETURN_STATUSES.filter(isClosed).sort();
    expect(closed).toEqual(["Cancelled", "Exchanged", "RefundRejected", "Refunded", "Rejected"]);
  });

  it("gives every status a tone and marks the refusals as danger", () => {
    for (const status of RETURN_STATUSES) expect(statusTone(status)).toBeTruthy();
    expect(statusTone("Rejected")).toBe("danger");
    expect(statusTone("RefundRejected")).toBe("danger");
    expect(statusTone("Refunded")).toBe("success");
    expect(statusTone("Cancelled")).toBe("outline");
  });

  it("places the happy path on the progress strip and leaves the refusals off it", () => {
    expect(["Requested", "Approved", "PickupScheduled", "PickedUp", "Received", "Inspected", "Refunded"].map((s) => customerStepIndex(s as never))).toEqual([0, 1, 2, 3, 4, 5, 6]);
    expect(customerStepIndex("Exchanged")).toBe(6);
    expect(customerStepIndex("Rejected")).toBe(-1);
    expect(customerStepIndex("Cancelled")).toBe(-1);
  });
});

describe("quantity limits", () => {
  const line = { returnable: true, returnableQuantity: "2" as number | string };

  it("allows what is left and nothing for blocked lines", () => {
    expect(maxQuantity(line)).toBe(2);
    expect(maxQuantity({ ...line, returnable: false })).toBe(0);
    expect(maxQuantity({ returnable: true, returnableQuantity: 0 })).toBe(0);
    expect(maxQuantity({ returnable: true, returnableQuantity: "x" })).toBe(0);
  });

  it("keeps a typed quantity inside the limit", () => {
    expect(clampQuantity(5, 2)).toBe(2);
    expect(clampQuantity(-3, 2)).toBe(0);
    expect(clampQuantity("abc", 2)).toBe(0);
    expect(clampQuantity("1", 2)).toBe(1);
    expect(clampQuantity(3, 0)).toBe(0);
  });

  it("turns Bengali digits into numbers", () => {
    expect(digitsOnly("২")).toBe("2");
    expect(digitsOnly("1a২")).toBe("12");
    expect(clampQuantity(digitsOnly("৩"), 5)).toBe(3);
  });

  it("builds only the lines above zero and names the replacement for an exchange", () => {
    const quantities = { a: 2, b: 0, c: 1 };
    expect(buildLines("Return", quantities, {}, { a: "va", c: "vc" })).toEqual([
      { orderItemId: "a", quantity: 2, exchangeVariantId: null },
      { orderItemId: "c", quantity: 1, exchangeVariantId: null },
    ]);
    expect(buildLines("Exchange", quantities, { c: "other" }, { a: "va", c: "vc" })).toEqual([
      { orderItemId: "a", quantity: 2, exchangeVariantId: "va" },
      { orderItemId: "c", quantity: 1, exchangeVariantId: "other" },
    ]);
  });
});

describe("pictures", () => {
  it("accepts JPEG, PNG and WebP up to 10 MB", () => {
    expect(photoProblem({ type: "image/png", size: 1000 })).toBeNull();
    expect(photoProblem({ type: "image/webp", size: 10 * 1024 * 1024 })).toBeNull();
    expect(photoProblem({ type: "image/gif", size: 1000 })).toBe("type");
    expect(photoProblem({ type: "application/pdf", size: 1000 })).toBe("type");
    expect(photoProblem({ type: "image/jpeg", size: 10 * 1024 * 1024 + 1 })).toBe("size");
  });

  it("needs 1000 px on the long side", () => {
    expect(photoSizeProblem(1000, 300)).toBeNull();
    expect(photoSizeProblem(300, 1200)).toBeNull();
    expect(photoSizeProblem(999, 999)).toBe("dimensions");
  });
});

describe("refund display", () => {
  it("lists items, delivery charge and return charge, skipping zero extras", () => {
    const only = refundRows({ items: "270", deliveryCharge: 0, returnCharge: 0, total: 270, final: false });
    expect(only.rows.map((r) => r.key)).toEqual(["items"]);
    expect(only.total).toBe(270);
    expect(only.final).toBe(false);

    const full = refundRows({ items: 270, deliveryCharge: 60, returnCharge: 50, total: 280, final: true });
    expect(full.rows).toEqual([
      { key: "items", amount: 270, sign: "+" },
      { key: "deliveryCharge", amount: 60, sign: "+" },
      { key: "returnCharge", amount: 50, sign: "-" },
    ]);
    expect(full.final).toBe(true);
  });

  it("estimates in whole paisa so three single units add up exactly", () => {
    const lines = [{ orderItemId: "t", refundPerUnit: 270 }, { orderItemId: "c", refundPerUnit: "90.10" }];
    expect(estimateRefund(lines, { t: 3, c: 0 })).toBe(810);
    expect(estimateRefund(lines, { t: 1, c: 3 })).toBe(540.3);
    expect(estimateRefund(lines, {})).toBe(0);
  });

  it("guesses the fault from the reason, as the API does", () => {
    expect(defaultFault("DAMAGED_ITEM")).toBe("Seller");
    expect(defaultFault("WRONG_ITEM")).toBe("Seller");
    expect(defaultFault("SIZE_FIT")).toBe("Customer");
    expect(defaultFault("OTHER")).toBe("Customer");
  });
});

describe("staff actions", () => {
  it("offers only the moves the state machine allows now, main step first", () => {
    expect(legalActions(["Approved", "Rejected"], "Return")).toEqual(["approve", "reject"]);
    expect(legalActions(["PickupScheduled"], "Return")).toEqual(["schedulePickup"]);
    expect(legalActions(["Refunded", "Exchanged", "RefundRejected"], "Return")).toEqual(["refund", "rejectRefund"]);
    expect(legalActions(["Refunded", "Exchanged", "RefundRejected"], "Exchange")).toEqual(["exchange", "rejectRefund"]);
    expect(legalActions(["Cancelled"], "Return")).toEqual([]);
    expect(legalActions([], "Return")).toEqual([]);
  });

  it("keeps only the actions the person holds the permission for", () => {
    const held = new Set(["returns.return.approve"]);
    expect(allowedActions(["Approved", "Rejected"], "Return", (p) => held.has(p))).toEqual(["approve"]);
    expect(allowedActions(["Approved", "Rejected"], "Return", () => false)).toEqual([]);
    expect(ACTION_PERMISSION.refund).toBe("returns.return.refund");
    expect(ACTION_PERMISSION.rejectRefund).toBe("returns.return.reject");
  });

  it("never lets a damaged unit go back on the shelf", () => {
    const damaged = withCondition(defaultInspect(), "Damaged");
    expect(damaged.restock).toBe(false);
    const back = withCondition(damaged, "Good");
    expect(back.condition).toBe("Good");
    expect(back.restock).toBe(true);
    expect(withCondition({ ...defaultInspect(), restock: false }, "Unopened").restock).toBe(false);
    expect(defaultInspect()).toEqual({ condition: "Good", restock: true, acceptRefund: true });
  });

  it("allows pickups from today to 60 days ahead", () => {
    expect(pickupBounds(new Date(2026, 10, 11))).toEqual({ min: "2026-11-11", max: "2027-01-10" });
  });
});
