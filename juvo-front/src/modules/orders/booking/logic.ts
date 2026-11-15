/**
 * Pure helpers for courier booking, the live screens and the desk preview (Step 10 front end). No state, no calls; unit tested in
 * tests/booking.test.ts.
 */
import type { BulkBookResult, Suggestion } from "@/modules/couriers/api";
import type { LiveEvent, LiveEventName } from "@/lib/realtime/use-live";

const num = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));

/* ---- Suggestions ----------------------------------------------------------------------------------------------------------- */

/** Eligible couriers first (the API already sorts them; the recommended one leads), the ones that cannot take the parcel after them. */
export function orderSuggestions(list: readonly Suggestion[]): { eligible: Suggestion[]; ineligible: Suggestion[] } {
  const eligible = list.filter((s) => s.eligible).sort((a, b) => Number(b.recommended) - Number(a.recommended));
  return { eligible, ineligible: list.filter((s) => !s.eligible) };
}

/** The courier to preselect: the recommended eligible one, else the first eligible, else nothing. Keeps a still-valid manual choice. */
export function defaultCourier(list: readonly Suggestion[], current?: string | null): string {
  const { eligible } = orderSuggestions(list);
  if (current && eligible.some((s) => s.courierId === current)) return current;
  return eligible[0]?.courierId ?? "";
}

/** Grams from the weight box (kilograms, up to 2 decimals). Empty means "the order's weight"; null means not a valid number. */
export function parseWeightKg(text: string): number | null | undefined {
  const value = text.trim().replace(",", ".");
  if (value === "") return undefined;
  if (!/^\d+(\.\d{1,2})?$/.test(value)) return null;
  const grams = Math.round(Number(value) * 1000);
  return grams >= 1 && grams <= 200_000 ? grams : null;
}

/** A dimension in whole centimetres, 1 to 300 like the API; empty is undefined; anything else is null. */
export function parseCm(text: string): number | null | undefined {
  const value = text.trim();
  if (value === "") return undefined;
  if (!/^\d{1,3}$/.test(value)) return null;
  const n = Number(value);
  return n >= 1 && n <= 300 ? n : null;
}

/** The booking body: only what the person filled in; the courier is left out when auto is wanted. */
export function buildBookRequest(input: { courierId: string; weightGrams?: number; lengthCm?: number; widthCm?: number; heightCm?: number; note: string }) {
  return {
    courierId: input.courierId || null,
    weightGrams: input.weightGrams ?? null,
    lengthCm: input.lengthCm ?? null,
    widthCm: input.widthCm ?? null,
    heightCm: input.heightCm ?? null,
    note: input.note.trim() || null,
  };
}

/* ---- Bulk booking ---------------------------------------------------------------------------------------------------------- */

/** Counts and the lines to show, failures first so the person sees what needs a hand. */
export function summarizeBulk(result: BulkBookResult): { ok: number; failed: number; rows: BulkBookResult["results"] } {
  const rows = [...result.results].sort((a, b) => Number(a.ok) - Number(b.ok));
  return { ok: num(result.succeeded), failed: num(result.failed), rows };
}

/** Only Packed orders can be booked: the ids that qualify out of a selection, and the rest. */
export function splitBookable(selection: readonly string[], statusOf: (id: string) => string | undefined): { bookable: string[]; skipped: string[] } {
  const bookable: string[] = [];
  const skipped: string[] = [];
  for (const id of selection) (statusOf(id) === "Packed" ? bookable : skipped).push(id);
  return { bookable: bookable.slice(0, 50), skipped: [...skipped, ...bookable.slice(50)] };
}

/* ---- Live events ----------------------------------------------------------------------------------------------------------- */

/** Events that change what the order list shows. */
export const LIST_EVENTS: LiveEventName[] = ["OrderStatusChanged", "PaymentStatusChanged", "ShipmentEventAdded", "NewOrder"];
/** Events that change one order page. */
export const DETAIL_EVENTS: LiveEventName[] = ["OrderStatusChanged", "PaymentStatusChanged", "ShipmentEventAdded", "ReturnStatusChanged"];

/** The text of a new-order toast, or null when the payload is not a usable new-order event. */
export function newOrderToast(event: LiveEvent): { number: string; total: number | null; orderId: string | null } | null {
  if (event.name !== "NewOrder" || typeof event.orderNumber !== "string") return null;
  const total = event.total === undefined || event.total === null ? null : Number(event.total);
  return { number: event.orderNumber, total: total !== null && Number.isFinite(total) ? total : null, orderId: typeof event.orderId === "string" ? event.orderId : null };
}

/** The branch to follow live: the active branch, or `all` only when the session is allowed to see every branch; otherwise none. */
export function liveBranch(access: { activeBranchId?: string | null; isAllBranches: boolean }): string | null {
  if (access.isAllBranches) return "all";
  return access.activeBranchId ?? null;
}

/* ---- Desk preview ---------------------------------------------------------------------------------------------------------- */

export type DeskQuoteInput = {
  customerMobile: string;
  source: string;
  branchId: string;
  address: Record<string, unknown> | null;
  lines: { variantId: string; quantity: number }[];
  paymentMethod: string;
  couponCode: string;
  discount: number;
  deliveryOverride: string;
  canDiscount: boolean;
  canOverride: boolean;
};

/** A preview can be asked for only when there is something to price and a place to deliver to. */
export function canQuote(input: Pick<DeskQuoteInput, "lines" | "address">): boolean {
  return input.lines.length > 0 && input.address !== null;
}

/** The exact body of `POST /orders/manual/quote` (same shape as create, so the totals match). Discount and override only with their permission. */
export function buildQuoteBody(input: DeskQuoteInput) {
  const override = input.canOverride && input.deliveryOverride.trim() !== "" ? Math.max(0, Number(input.deliveryOverride) || 0) : null;
  return {
    customerMobile: input.customerMobile,
    customerName: null,
    customerEmail: null,
    source: input.source,
    branchId: input.branchId || null,
    address: input.address,
    lines: input.lines.map((l) => ({ variantId: l.variantId, sku: null, quantity: l.quantity })),
    paymentMethod: input.paymentMethod,
    couponCode: input.couponCode.trim() ? input.couponCode.trim().toUpperCase() : null,
    discount: input.canDiscount && input.discount > 0 ? input.discount : null,
    deliveryChargeOverride: override,
    customerNote: null,
    internalNote: null,
    idempotencyKey: null,
    confirmDuplicate: false,
  };
}
