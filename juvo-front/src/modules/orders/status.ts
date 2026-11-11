import type { DeliveryStatus, FraudState, OrderStatus, PaymentStatus, TimelineEntry, OrderTrack } from "./api";

/**
 * Pure helpers for the order screens: status colours, the delivery progress line, the manual order totals, and small input
 * clean-ups. They hold no state and call nothing, so they are unit tested (tests/orders.test.ts).
 */
export type Tone = "neutral" | "brand" | "success" | "warning" | "danger" | "info" | "outline";

export function orderStatusTone(status: OrderStatus): Tone {
  switch (status) {
    case "Placed":
      return "info";
    case "Confirmed":
    case "Packed":
      return "brand";
    case "HandedToCourier":
    case "InTransit":
    case "OutForDelivery":
      return "warning";
    case "Delivered":
      return "success";
    case "Cancelled":
    case "DeliveryFailed":
      return "danger";
    case "ReturnInProgress":
    case "Returned":
      return "neutral";
  }
}

export function paymentStatusTone(status: PaymentStatus): Tone {
  switch (status) {
    case "Paid":
    case "Settled":
    case "CollectedByCourier":
      return "success";
    case "Pending":
    case "UnderReview":
      return "warning";
    case "Failed":
    case "Rejected":
    case "Cancelled":
      return "danger";
    case "PartiallyRefunded":
    case "Refunded":
      return "neutral";
  }
}

export function deliveryStatusTone(status: DeliveryStatus): Tone {
  switch (status) {
    case "Delivered":
      return "success";
    case "Failed":
    case "Cancelled":
      return "danger";
    case "NotShipped":
    case "Returned":
      return "neutral";
    case "ReadyToShip":
      return "brand";
    default:
      return "warning";
  }
}

export function fraudStateTone(state: FraudState): Tone {
  switch (state) {
    case "Flagged":
    case "InReview":
    case "AdvanceRequested":
      return "danger";
    case "Approved":
      return "success";
    case "Rejected":
      return "neutral";
    case "None":
      return "outline";
  }
}

/** Held orders wait for a person to decide before they can be confirmed. */
export const isHeldState = (state: FraudState) => state === "Flagged" || state === "InReview" || state === "AdvanceRequested";

/** The normal road of an order, shown as a progress line. Cancelled, failed and returned orders leave it. */
export const HAPPY_PATH: OrderStatus[] = ["Placed", "Confirmed", "Packed", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered"];

export type ProgressStep = { status: OrderStatus; state: "done" | "current" | "todo"; at: string | null };

/**
 * The progress line for an order: which steps of the normal road were reached (with the time), which one is current, which are
 * still ahead. An order that left the road (cancelled, delivery failed, return in progress, returned) shows what it reached and
 * the end status in `extra`.
 */
export function progressSteps(status: OrderStatus, timeline: TimelineEntry[]): { steps: ProgressStep[]; extra: { status: OrderStatus; at: string | null } | null } {
  const reached = new Map<string, string>();
  for (const entry of [...timeline].filter((e) => e.track === "Order").sort((a, b) => a.at.localeCompare(b.at))) reached.set(entry.to, entry.at);
  const onRoad = HAPPY_PATH.includes(status);
  const steps = HAPPY_PATH.map<ProgressStep>((s) => ({
    status: s,
    at: reached.get(s) ?? null,
    state: onRoad ? (s === status ? (s === "Delivered" ? "done" : "current") : reached.has(s) || HAPPY_PATH.indexOf(s) < HAPPY_PATH.indexOf(status) ? "done" : "todo") : reached.has(s) ? "done" : "todo",
  }));
  return { steps, extra: onRoad ? null : { status, at: reached.get(status) ?? null } };
}

/** The history of one track, oldest first. */
export function trackEntries(timeline: TimelineEntry[], track: OrderTrack): TimelineEntry[] {
  return timeline.filter((e) => e.track === track).sort((a, b) => a.at.localeCompare(b.at));
}

/** What staff may do next, from the API's list of allowed next statuses. Delivery steps are the ones that are not confirm, pack or cancel. */
export function actionsFrom(next: readonly string[]) {
  const set = new Set(next);
  const steps = (["HandedToCourier", "InTransit", "OutForDelivery", "Delivered", "DeliveryFailed", "ReturnInProgress", "Returned"] as const).filter((s) => set.has(s));
  return { confirm: set.has("Confirmed"), pack: set.has("Packed"), cancel: set.has("Cancelled"), steps };
}

/** Totals for the social order desk preview: goods from the shop prices, a manual discount that can never exceed the goods, and the delivery charge. */
export function deskTotals(input: { lines: { price: number; quantity: number }[]; discount: number; delivery: number }) {
  const subtotal = round2(input.lines.reduce((sum, l) => sum + l.price * l.quantity, 0));
  const discount = round2(Math.min(Math.max(input.discount, 0), subtotal));
  const delivery = round2(Math.max(input.delivery, 0));
  return { subtotal, discount, delivery, grandTotal: round2(subtotal - discount + delivery) };
}

export const round2 = (value: number) => Math.round((value + Number.EPSILON) * 100) / 100;

/** Digits only in 0-9 (Bengali digits turned over), keeping a leading plus. */
export function normalizeDigits(value: string): string {
  return value.replace(/[০-৯]/g, (ch) => String(ch.charCodeAt(0) - 0x09e6));
}

const BD_MOBILE = /^(?:\+?88)?01[3-9]\d{8}$/;

/** A Bangladesh mobile number as typed (spaces, dashes and Bengali digits allowed). */
export function isBdMobile(value: string): boolean {
  return BD_MOBILE.test(normalizeDigits(value).replace(/[\s-]/g, ""));
}

/** "01712345678" for display and sending; leaves anything that is not a Bangladesh mobile as typed. */
export function cleanMobile(value: string): string {
  const digits = normalizeDigits(value).replace(/[\s-]/g, "");
  return BD_MOBILE.test(digits) ? digits.replace(/^\+?88/, "") : digits;
}

/** Link that opens the public tracking page for a token. */
export const trackingLink = (token: string) => `/track?token=${encodeURIComponent(token)}`;

/** One key per attempt to place an order; kept while the attempt is retried so the same order comes back. */
export function newKey(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) return crypto.randomUUID();
  return `k${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`;
}

const DEVICE_KEY = "juvo_device";

/** A stable random id for this browser (used only by the fraud rules). Without storage a new one is made each time. */
export function getDeviceId(): string {
  try {
    const found = window.localStorage.getItem(DEVICE_KEY);
    if (found) return found;
    const id = newKey();
    window.localStorage.setItem(DEVICE_KEY, id);
    return id;
  } catch {
    return newKey();
  }
}

/** Start of the first day and the start of the day after the last day, as ISO text, for the API's from/to filter. */
export function dayRange(from: string | undefined, to: string | undefined): { from?: string; to?: string } {
  const start = from ? new Date(`${from}T00:00:00`) : null;
  const end = to ? new Date(`${to}T00:00:00`) : null;
  if (end) end.setDate(end.getDate() + 1);
  return { from: start && !Number.isNaN(start.getTime()) ? start.toISOString() : undefined, to: end && !Number.isNaN(end.getTime()) ? end.toISOString() : undefined };
}
