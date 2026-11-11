import type { Schemas } from "@/lib/api/types";

/** Pure rules of the returns screens (no React, no network), so they can be tested on their own. */

export type ReturnStatus = Schemas["ReturnStatus"];
export type ReturnKind = Schemas["ReturnKind"];
export type ReturnFault = Schemas["ReturnFault"];
export type RefundMethod = Schemas["RefundMethod"];
export type ItemCondition = NonNullable<Schemas["ItemCondition"]>;

export const RETURN_STATUSES: ReturnStatus[] = ["Requested", "Approved", "PickupScheduled", "PickedUp", "Received", "Inspected", "Refunded", "Exchanged", "Rejected", "RefundRejected", "Cancelled"];
export const RETURN_KINDS: ReturnKind[] = ["Return", "Exchange"];
export const RETURN_FAULTS: ReturnFault[] = ["Customer", "Seller"];
export const REFUND_METHODS: RefundMethod[] = ["OriginalPayment", "Cash", "BankTransfer", "Wallet"];
export const CONDITIONS: ItemCondition[] = ["Unopened", "Good", "Damaged"];

/** The five states nothing can follow (docs/returns-api.md section 3). */
const FINAL = new Set<ReturnStatus>(["Rejected", "Refunded", "Exchanged", "RefundRejected", "Cancelled"]);

export const isClosed = (status: ReturnStatus) => FINAL.has(status);

export type Tone = "info" | "warning" | "success" | "danger" | "outline" | "neutral";

/** Colour family of a status badge. The meaning is always also in the badge text (and the danger ones carry an icon). */
export function statusTone(status: ReturnStatus): Tone {
  switch (status) {
    case "Requested":
      return "warning";
    case "Approved":
    case "PickupScheduled":
    case "PickedUp":
    case "Received":
    case "Inspected":
      return "info";
    case "Refunded":
    case "Exchanged":
      return "success";
    case "Rejected":
    case "RefundRejected":
      return "danger";
    case "Cancelled":
      return "outline";
  }
}

export const isDangerStatus = (status: ReturnStatus) => statusTone(status) === "danger";

/** The happy path as a progress strip for the customer: where the request stands (index) of the steps shown. */
export const CUSTOMER_STEPS = ["Requested", "Approved", "PickupScheduled", "PickedUp", "Received", "Inspected", "Done"] as const;
export type CustomerStep = (typeof CUSTOMER_STEPS)[number];

export function customerStepIndex(status: ReturnStatus): number {
  switch (status) {
    case "Requested":
      return 0;
    case "Approved":
      return 1;
    case "PickupScheduled":
      return 2;
    case "PickedUp":
      return 3;
    case "Received":
      return 4;
    case "Inspected":
      return 5;
    case "Refunded":
    case "Exchanged":
      return 6;
    default:
      return -1; // Rejected, RefundRejected, Cancelled: the strip does not apply
  }
}

/* ---- Request flow ---------------------------------------------------------------------------------------------------------- */

export type Line = { returnable: boolean; returnableQuantity: number | string; blockedReason?: string | null };

/** How many units of a line can be put on a request: nothing when the line is blocked. */
export function maxQuantity(line: Line): number {
  if (!line.returnable) return 0;
  const n = Number(line.returnableQuantity);
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}

/** Keeps a typed quantity inside 0..max (anything else, including text, becomes 0). */
export function clampQuantity(value: string | number, max: number): number {
  const n = typeof value === "number" ? value : Number(String(value).replace(/\D/g, ""));
  if (!Number.isFinite(n) || n < 0) return 0;
  return Math.min(Math.floor(n), Math.max(0, max));
}

/** Same Bengali-digit tolerance as the quantity inputs elsewhere: "২" becomes "2". */
export function digitsOnly(value: string): string {
  return value.replace(/[০-৯]/g, (ch) => String(ch.charCodeAt(0) - 0x09e6)).replace(/\D/g, "").slice(0, 3);
}

export type Picked = { orderItemId: string; quantity: number; exchangeVariantId: string | null };

/** The lines to send: only units above zero, and for an exchange every line names its replacement (the same variant by default). */
export function buildLines(kind: ReturnKind, quantities: Record<string, number>, variants: Record<string, string | null>, original: Record<string, string>): Picked[] {
  return Object.entries(quantities)
    .filter(([, q]) => q > 0)
    .map(([orderItemId, quantity]) => ({
      orderItemId,
      quantity,
      exchangeVariantId: kind === "Exchange" ? (variants[orderItemId] ?? original[orderItemId] ?? null) : null,
    }));
}

/** Pictures: type, size, and how many. The 1000 px rule is checked after the file is read (`photoSizeProblem`). */
export const PHOTO_TYPES = ["image/jpeg", "image/png", "image/webp"] as const;
export const PHOTO_MAX_BYTES = 10 * 1024 * 1024;
export const PHOTO_MAX_COUNT = 5;
export const PHOTO_MIN_SIDE = 1000;

export type PhotoProblem = "type" | "size" | "dimensions";

export function photoProblem(file: { type: string; size: number }): PhotoProblem | null {
  if (!(PHOTO_TYPES as readonly string[]).includes(file.type)) return "type";
  if (file.size > PHOTO_MAX_BYTES) return "size";
  return null;
}

export const photoSizeProblem = (width: number, height: number): PhotoProblem | null => (Math.max(width, height) < PHOTO_MIN_SIDE ? "dimensions" : null);

/** The reasons whose default blame is the shop (the API decides; the screen only hints that no return charge applies). */
const SELLER_REASONS = new Set(["DAMAGED_ITEM", "WRONG_ITEM", "NOT_AS_DESCRIBED", "QUALITY_ISSUE"]);
export const defaultFault = (reasonCode: string): ReturnFault => (SELLER_REASONS.has(reasonCode) ? "Seller" : "Customer");

/** A fresh key for one submit attempt; reused for retries of the same attempt so a double tap never makes two requests. */
export function newIdempotencyKey(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) return `ret-${crypto.randomUUID()}`;
  return `ret-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

/* ---- Money ----------------------------------------------------------------------------------------------------------------- */

export type Breakdown = { items: number | string; deliveryCharge: number | string; returnCharge: number | string; total: number | string; final: boolean };

const num = (v: number | string | null | undefined) => {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
};

export type RefundRow = { key: "items" | "deliveryCharge" | "returnCharge"; amount: number; sign: "+" | "-" };

/** The rows of a refund box: the items, the delivery charge when it comes back, a return charge taken off. Zero rows (except items) are left out. */
export function refundRows(refund: Breakdown): { rows: RefundRow[]; total: number; final: boolean } {
  const rows: RefundRow[] = [{ key: "items", amount: num(refund.items), sign: "+" }];
  if (num(refund.deliveryCharge) > 0) rows.push({ key: "deliveryCharge", amount: num(refund.deliveryCharge), sign: "+" });
  if (num(refund.returnCharge) > 0) rows.push({ key: "returnCharge", amount: num(refund.returnCharge), sign: "-" });
  return { rows, total: num(refund.total), final: refund.final };
}

/** Estimate shown on the request screen: the units chosen times the price paid per unit (the server fixes the final figure at inspection). */
export function estimateRefund(lines: { orderItemId: string; refundPerUnit: number | string }[], quantities: Record<string, number>): number {
  const cents = lines.reduce((sum, l) => sum + Math.round(num(l.refundPerUnit) * 100) * (quantities[l.orderItemId] ?? 0), 0);
  return cents / 100;
}

/* ---- Staff actions --------------------------------------------------------------------------------------------------------- */

export type StageAction = "approve" | "reject" | "schedulePickup" | "pickedUp" | "received" | "inspect" | "refund" | "exchange" | "rejectRefund";

/** The permission each action needs (docs/returns-api.md section 2). */
export const ACTION_PERMISSION: Record<StageAction, string> = {
  approve: "returns.return.approve",
  reject: "returns.return.reject",
  schedulePickup: "returns.return.manage",
  pickedUp: "returns.return.manage",
  received: "returns.return.receive",
  inspect: "returns.return.inspect",
  refund: "returns.return.refund",
  exchange: "returns.return.exchange",
  rejectRefund: "returns.return.reject",
};

/** The order the buttons appear in: the main step first, the "no" options after it. */
const ACTION_ORDER: StageAction[] = ["approve", "schedulePickup", "pickedUp", "received", "inspect", "refund", "exchange", "reject", "rejectRefund"];

/**
 * The actions that are legal now, from the server's `nextStatuses` (the state machine lives in the API) and the kind of the return:
 * "Refunded" is a refund (kind Return), "Exchanged" an exchange (kind Exchange). `Cancelled` is the customer's own move, so staff get none.
 */
export function legalActions(next: readonly string[], kind: ReturnKind): StageAction[] {
  const found = new Set<StageAction>();
  for (const status of next) {
    switch (status) {
      case "Approved":
        found.add("approve");
        break;
      case "Rejected":
        found.add("reject");
        break;
      case "PickupScheduled":
        found.add("schedulePickup");
        break;
      case "PickedUp":
        found.add("pickedUp");
        break;
      case "Received":
        found.add("received");
        break;
      case "Inspected":
        found.add("inspect");
        break;
      case "Refunded":
        if (kind === "Return") found.add("refund");
        break;
      case "Exchanged":
        if (kind === "Exchange") found.add("exchange");
        break;
      case "RefundRejected":
        found.add("rejectRefund");
        break;
    }
  }
  return ACTION_ORDER.filter((a) => found.has(a));
}

/** Actions the person may run: legal in the state machine AND held as a permission in the return's branch. */
export function allowedActions(next: readonly string[], kind: ReturnKind, can: (permission: string) => boolean): StageAction[] {
  return legalActions(next, kind).filter((a) => can(ACTION_PERMISSION[a]));
}

/** A damaged unit can never go back on the shelf (the API refuses it); the form switches restock off and locks it. */
export function restockAllowed(condition: ItemCondition): boolean {
  return condition !== "Damaged";
}

export type InspectDraft = { condition: ItemCondition; restock: boolean; acceptRefund: boolean };

export const defaultInspect = (): InspectDraft => ({ condition: "Good", restock: true, acceptRefund: true });

export function withCondition(draft: InspectDraft, condition: ItemCondition): InspectDraft {
  if (!restockAllowed(condition)) return { ...draft, condition, restock: false };
  // Moving back from Damaged to a sellable state offers the shelf again (the person can still switch it off).
  return { ...draft, condition, restock: restockAllowed(draft.condition) ? draft.restock : true };
}

/** Today as yyyy-mm-dd in the local calendar, and the pickup window the API accepts (today to 60 days ahead). */
export function pickupBounds(now: Date = new Date()): { min: string; max: string } {
  const fmt = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  const max = new Date(now);
  max.setDate(max.getDate() + 60);
  return { min: fmt(now), max: fmt(max) };
}

/**
 * The count chips of the queue. A chip with a `status` is a filter button; `awaitingRefund` and `awaitingExchange` both mean Inspected,
 * told apart by the kind. "open" and "inTransit" (two statuses at once) are shown as plain counts.
 */
export const SUMMARY_CHIPS: { key: "open" | "awaitingApproval" | "awaitingPickup" | "inTransit" | "awaitingInspection" | "awaitingRefund" | "awaitingExchange"; status?: ReturnStatus; kind?: ReturnKind }[] = [
  { key: "open" },
  { key: "awaitingApproval", status: "Requested" },
  { key: "awaitingPickup", status: "Approved" },
  { key: "inTransit" },
  { key: "awaitingInspection", status: "Received" },
  { key: "awaitingRefund", status: "Inspected", kind: "Return" },
  { key: "awaitingExchange", status: "Inspected", kind: "Exchange" },
];
