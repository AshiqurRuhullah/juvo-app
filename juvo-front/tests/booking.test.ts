import { describe, expect, it } from "vitest";
import {
  buildBookRequest,
  buildQuoteBody,
  canQuote,
  defaultCourier,
  DETAIL_EVENTS,
  LIST_EVENTS,
  liveBranch,
  newOrderToast,
  orderSuggestions,
  parseCm,
  parseWeightKg,
  splitBookable,
  summarizeBulk,
  type DeskQuoteInput,
} from "@/modules/orders/booking/logic";
import type { BulkBookResult, Suggestion } from "@/modules/couriers/api";

const s = (over: Partial<Suggestion>): Suggestion => ({ courierId: "x", code: "X", name: "X", provider: "SANDBOX", eligible: true, reason: null, cost: 60, daysMin: 1, daysMax: 3, recommended: false, ...over });

describe("courier suggestions", () => {
  const list = [s({ courierId: "no", eligible: false, reason: "Not served" }), s({ courierId: "b" }), s({ courierId: "a", recommended: true })];

  it("lists eligible couriers first with the recommended one leading, then the ones that cannot take the parcel", () => {
    const grouped = orderSuggestions(list);
    expect(grouped.eligible.map((x) => x.courierId)).toEqual(["a", "b"]);
    expect(grouped.ineligible.map((x) => x.courierId)).toEqual(["no"]);
  });

  it("preselects the recommended courier and keeps a still-valid manual choice", () => {
    expect(defaultCourier(list)).toBe("a");
    expect(defaultCourier(list, "b")).toBe("b");
    expect(defaultCourier(list, "no")).toBe("a"); // an ineligible choice is dropped
    expect(defaultCourier(list, "gone")).toBe("a");
  });

  it("selects nothing when no courier is eligible", () => {
    expect(defaultCourier([s({ eligible: false })])).toBe("");
    expect(defaultCourier([])).toBe("");
  });
});

describe("booking form values", () => {
  it("reads kilograms into grams, empty as unset and nonsense as invalid", () => {
    expect(parseWeightKg("")).toBeUndefined();
    expect(parseWeightKg("0.6")).toBe(600);
    expect(parseWeightKg("1,25")).toBe(1250);
    expect(parseWeightKg("abc")).toBeNull();
    expect(parseWeightKg("0")).toBeNull();
    expect(parseWeightKg("1.234")).toBeNull();
  });

  it("accepts whole centimetres from 1 to 300", () => {
    expect(parseCm("")).toBeUndefined();
    expect(parseCm("30")).toBe(30);
    expect(parseCm("0")).toBeNull();
    expect(parseCm("301")).toBeNull();
    expect(parseCm("2.5")).toBeNull();
  });

  it("sends only what was filled in", () => {
    expect(buildBookRequest({ courierId: "a", note: "  " })).toEqual({ courierId: "a", weightGrams: null, lengthCm: null, widthCm: null, heightCm: null, note: null });
    expect(buildBookRequest({ courierId: "", weightGrams: 900, lengthCm: 20, note: " fragile " })).toMatchObject({ courierId: null, weightGrams: 900, lengthCm: 20, note: "fragile" });
  });
});

describe("bulk booking", () => {
  const result = {
    succeeded: 1,
    failed: 2,
    results: [
      { orderId: "1", orderNumber: "JV-1", ok: true, shipmentId: "s", consignmentId: "C1", errorCode: null, message: null },
      { orderId: "2", orderNumber: "JV-2", ok: false, shipmentId: null, consignmentId: null, errorCode: "SHIPMENT.ORDER_NOT_READY", message: "x" },
      { orderId: "3", orderNumber: "JV-3", ok: false, shipmentId: null, consignmentId: null, errorCode: "COURIER.NO_COURIER_AVAILABLE", message: "y" },
    ],
  } as BulkBookResult;

  it("counts the outcome and shows failures first", () => {
    const summary = summarizeBulk(result);
    expect(summary.ok).toBe(1);
    expect(summary.failed).toBe(2);
    expect(summary.rows.map((r) => r.orderId)).toEqual(["2", "3", "1"]);
  });

  it("books only packed orders, at most 50, and reports the rest as skipped", () => {
    const status = (id: string) => (id.startsWith("p") ? "Packed" : "Placed");
    const picked = splitBookable(["p1", "x2", "p3"], status);
    expect(picked.bookable).toEqual(["p1", "p3"]);
    expect(picked.skipped).toEqual(["x2"]);
    const many = Array.from({ length: 53 }, (_, i) => `p${i}`);
    const capped = splitBookable(many, status);
    expect(capped.bookable).toHaveLength(50);
    expect(capped.skipped).toHaveLength(3);
  });
});

describe("live events", () => {
  it("lets the list follow new orders and the detail page follow its own order's changes", () => {
    expect(LIST_EVENTS).toContain("NewOrder");
    expect(DETAIL_EVENTS).not.toContain("NewOrder");
    for (const e of ["OrderStatusChanged", "PaymentStatusChanged", "ShipmentEventAdded"] as const) {
      expect(LIST_EVENTS).toContain(e);
      expect(DETAIL_EVENTS).toContain(e);
    }
    expect(DETAIL_EVENTS).toContain("ReturnStatusChanged");
  });

  it("reads a new-order event into toast data and ignores other or malformed events", () => {
    expect(newOrderToast({ name: "NewOrder", orderNumber: "JV-HO-1", total: 1060, orderId: "o1" })).toEqual({ number: "JV-HO-1", total: 1060, orderId: "o1" });
    expect(newOrderToast({ name: "NewOrder", orderNumber: "JV-HO-1" })).toEqual({ number: "JV-HO-1", total: null, orderId: null });
    expect(newOrderToast({ name: "OrderStatusChanged", orderNumber: "JV-HO-1" })).toBeNull();
    expect(newOrderToast({ name: "NewOrder" })).toBeNull();
  });

  it("follows every branch only when the session sees them all", () => {
    expect(liveBranch({ activeBranchId: "b1", isAllBranches: false })).toBe("b1");
    expect(liveBranch({ activeBranchId: "b1", isAllBranches: true })).toBe("all");
    expect(liveBranch({ activeBranchId: null, isAllBranches: false })).toBeNull();
  });
});

describe("desk preview request", () => {
  const input: DeskQuoteInput = {
    customerMobile: "+8801712345678",
    source: "Facebook",
    branchId: "",
    address: { recipientName: "A" },
    lines: [{ variantId: "v1", quantity: 2 }],
    paymentMethod: "COD",
    couponCode: " desk10 ",
    discount: 50,
    deliveryOverride: "70",
    canDiscount: true,
    canOverride: true,
  };

  it("asks only when there are items and an address", () => {
    expect(canQuote(input)).toBe(true);
    expect(canQuote({ ...input, lines: [] })).toBe(false);
    expect(canQuote({ ...input, address: null })).toBe(false);
  });

  it("builds the same body as creating the order, with the coupon upper-cased", () => {
    const body = buildQuoteBody(input);
    expect(body.couponCode).toBe("DESK10");
    expect(body.discount).toBe(50);
    expect(body.deliveryChargeOverride).toBe(70);
    expect(body.branchId).toBeNull();
    expect(body.lines).toEqual([{ variantId: "v1", sku: null, quantity: 2 }]);
    expect(body.confirmDuplicate).toBe(false);
  });

  it("leaves the discount and delivery override out without their permissions", () => {
    const body = buildQuoteBody({ ...input, canDiscount: false, canOverride: false, couponCode: "" });
    expect(body.discount).toBeNull();
    expect(body.deliveryChargeOverride).toBeNull();
    expect(body.couponCode).toBeNull();
  });
});
