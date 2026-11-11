import { describe, expect, it } from "vitest";
import type { TimelineEntry } from "@/modules/orders/api";
import {
  actionsFrom,
  cleanMobile,
  dayRange,
  deliveryStatusTone,
  deskTotals,
  fraudStateTone,
  HAPPY_PATH,
  isBdMobile,
  isHeldState,
  newKey,
  normalizeDigits,
  orderStatusTone,
  paymentStatusTone,
  progressSteps,
  trackEntries,
  trackingLink,
} from "@/modules/orders/status";

const entry = (track: TimelineEntry["track"], to: string, at: string, from: string | null = null): TimelineEntry => ({ track, from, to, reason: null, actorName: null, at });

describe("status tones", () => {
  it("every status has a tone and bad outcomes are danger", () => {
    expect(orderStatusTone("Cancelled")).toBe("danger");
    expect(orderStatusTone("DeliveryFailed")).toBe("danger");
    expect(orderStatusTone("Delivered")).toBe("success");
    expect(paymentStatusTone("Paid")).toBe("success");
    expect(paymentStatusTone("Failed")).toBe("danger");
    expect(paymentStatusTone("Pending")).toBe("warning");
    expect(deliveryStatusTone("OutForDelivery")).toBe("warning");
    expect(fraudStateTone("Flagged")).toBe("danger");
    expect(fraudStateTone("None")).toBe("outline");
  });

  it("held means flagged, in review or advance requested", () => {
    expect(["Flagged", "InReview", "AdvanceRequested"].every((s) => isHeldState(s as never))).toBe(true);
    expect(["None", "Approved", "Rejected"].some((s) => isHeldState(s as never))).toBe(false);
  });
});

describe("progress line", () => {
  const timeline = [entry("Order", "Placed", "2026-11-01T10:00:00Z"), entry("Order", "Confirmed", "2026-11-01T11:00:00Z", "Placed"), entry("Payment", "Pending", "2026-11-01T10:00:00Z")];

  it("marks reached steps done, the current one current and the rest todo", () => {
    const { steps, extra } = progressSteps("Confirmed", timeline);
    expect(extra).toBeNull();
    expect(steps.map((s) => s.state)).toEqual(["done", "current", "todo", "todo", "todo", "todo", "todo"]);
    expect(steps[1]!.at).toBe("2026-11-01T11:00:00Z");
    expect(steps).toHaveLength(HAPPY_PATH.length);
  });

  it("a delivered order is complete", () => {
    expect(progressSteps("Delivered", timeline).steps.at(-1)!.state).toBe("done");
  });

  it("a cancelled order shows what it reached and the end status", () => {
    const { steps, extra } = progressSteps("Cancelled", [...timeline, entry("Order", "Cancelled", "2026-11-01T12:00:00Z", "Confirmed")]);
    expect(steps.map((s) => s.state)).toEqual(["done", "done", "todo", "todo", "todo", "todo", "todo"]);
    expect(extra).toEqual({ status: "Cancelled", at: "2026-11-01T12:00:00Z" });
  });

  it("keeps one track and sorts oldest first", () => {
    const rows = trackEntries([entry("Order", "B", "2026-11-02T00:00:00Z"), entry("Payment", "X", "2026-11-01T00:00:00Z"), entry("Order", "A", "2026-11-01T00:00:00Z")], "Order");
    expect(rows.map((r) => r.to)).toEqual(["A", "B"]);
  });
});

describe("what staff can do next", () => {
  it("splits confirm, pack, cancel and delivery steps", () => {
    expect(actionsFrom(["Confirmed", "Cancelled"])).toEqual({ confirm: true, pack: false, cancel: true, steps: [] });
    expect(actionsFrom(["OutForDelivery"]).steps).toEqual(["OutForDelivery"]);
    expect(actionsFrom(["Delivered", "DeliveryFailed"]).steps).toEqual(["Delivered", "DeliveryFailed"]);
    expect(actionsFrom([])).toEqual({ confirm: false, pack: false, cancel: false, steps: [] });
  });
});

describe("desk totals", () => {
  it("adds goods and delivery, takes off the discount", () => {
    expect(deskTotals({ lines: [{ price: 1250.5, quantity: 2 }, { price: 300, quantity: 1 }], discount: 100, delivery: 80 })).toEqual({ subtotal: 2801, discount: 100, delivery: 80, grandTotal: 2781 });
  });

  it("never lets the discount pass the goods or go below zero, and rounds to paisa", () => {
    expect(deskTotals({ lines: [{ price: 100, quantity: 1 }], discount: 500, delivery: 60 }).grandTotal).toBe(60);
    expect(deskTotals({ lines: [{ price: 100, quantity: 1 }], discount: -5, delivery: -5 })).toEqual({ subtotal: 100, discount: 0, delivery: 0, grandTotal: 100 });
    expect(deskTotals({ lines: [{ price: 0.1, quantity: 3 }], discount: 0, delivery: 0.2 }).grandTotal).toBe(0.5);
  });
});

describe("mobile numbers", () => {
  it("turns Bengali digits into ASCII", () => {
    expect(normalizeDigits("০১৭১২৩৪৫৬৭৮")).toBe("01712345678");
  });
  it("accepts Bangladesh mobiles in common spellings", () => {
    for (const v of ["01712345678", "+8801712345678", "8801712345678", "017 1234-5678", "০১৭১২৩৪৫৬৭৮"]) expect(isBdMobile(v), v).toBe(true);
    for (const v of ["0171234567", "02712345678", "abc", ""]) expect(isBdMobile(v), v).toBe(false);
  });
  it("cleans to the national form", () => {
    expect(cleanMobile("+880 1712-345678")).toBe("01712345678");
    expect(cleanMobile("12345")).toBe("12345");
  });
});

describe("misc", () => {
  it("builds the tracking link and unique keys", () => {
    expect(trackingLink("a b/c")).toBe("/track?token=a%20b%2Fc");
    expect(newKey()).not.toBe(newKey());
  });
  it("turns a date range into inclusive ISO bounds", () => {
    const { from, to } = dayRange("2026-11-01", "2026-11-01");
    expect(new Date(to!).getTime() - new Date(from!).getTime()).toBeGreaterThanOrEqual(23 * 3600 * 1000);
    expect(dayRange(undefined, undefined)).toEqual({ from: undefined, to: undefined });
    expect(dayRange("nope", undefined).from).toBeUndefined();
  });
});
