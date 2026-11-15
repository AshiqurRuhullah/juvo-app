/**
 * Pure helpers for the courier screens: no state, no calls, unit tested (tests/couriers.test.ts).
 */

export const SHIPMENT_STATUSES = ["Booked", "PickedUp", "InTransit", "OutForDelivery", "Delivered", "DeliveryFailed", "Returned", "Cancelled"] as const;
export type ShipmentStatus = (typeof SHIPMENT_STATUSES)[number];

export const SETTLEMENT_STATUSES = ["Open", "Reconciled", "Settled", "Cancelled"] as const;
export type SettlementStatus = (typeof SETTLEMENT_STATUSES)[number];

/** Colour family of a parcel status, used by the badge. Failure is always shown with an icon and text next to the colour. */
export type Tone = "neutral" | "info" | "success" | "warning" | "danger";

export function shipmentTone(status: string): Tone {
  switch (status) {
    case "Delivered":
      return "success";
    case "DeliveryFailed":
      return "danger";
    case "Returned":
    case "Cancelled":
      return "neutral";
    case "Booked":
      return "warning";
    default:
      return "info";
  }
}

export function settlementTone(status: string): Tone {
  switch (status) {
    case "Settled":
      return "success";
    case "Reconciled":
      return "info";
    case "Open":
      return "warning";
    default:
      return "neutral";
  }
}

/** A parcel the courier has not finished with: bookings and journeys that can still change. */
export const isOpenShipment = (status: string) => !["Delivered", "Returned", "Cancelled"].includes(status);

/** The five steps of a normal journey, in order, for the progress bar. Failed and cancelled parcels are shown beside it. */
export const JOURNEY: readonly ShipmentStatus[] = ["Booked", "PickedUp", "InTransit", "OutForDelivery", "Delivered"];

/** 0-based position in the journey; -1 for parcels off the normal path. A failed delivery sits at "out for delivery". */
export function journeyIndex(status: string): number {
  if (status === "DeliveryFailed") return JOURNEY.indexOf("OutForDelivery");
  return JOURNEY.indexOf(status as ShipmentStatus);
}

/** Money with two decimals and the BDT symbol, the same for every courier screen. */
export function formatMoney(value: number | null | undefined, locale = "en"): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "-";
  return new Intl.NumberFormat(locale === "bn" ? "bn-BD" : "en-BD", { style: "currency", currency: "BDT", currencyDisplay: "narrowSymbol" }).format(value);
}

/** Grams as "600 g" or "1.25 kg". */
export function formatWeight(grams: number | null | undefined): string {
  if (grams === null || grams === undefined) return "-";
  return grams >= 1000 ? `${Number((grams / 1000).toFixed(2))} kg` : `${grams} g`;
}

/** What the courier would charge the shop: the base rate up to the base weight, then the extra rate per started kilogram. */
export function estimateCourierCost(baseRate: number, perExtraKgRate: number, baseUpToGrams: number, weightGrams: number): number {
  if (weightGrams <= baseUpToGrams) return round2(baseRate);
  const extraKg = Math.ceil((weightGrams - baseUpToGrams) / 1000);
  return round2(baseRate + extraKg * perExtraKgRate);
}

const round2 = (n: number) => Math.round(n * 100) / 100;

/** Short, short-of or over-paid: difference of received against expected, negative is short. */
export type Variance = "exact" | "short" | "over";
export function varianceOf(expected: number, received: number): Variance {
  const diff = round2(received - expected);
  return diff === 0 ? "exact" : diff < 0 ? "short" : "over";
}

/** Two decimals at most, never negative: what the reconcile endpoint accepts. */
export function isValidReceived(value: string): boolean {
  if (!/^\d+(\.\d{1,2})?$/.test(value.trim())) return false;
  return Number(value) >= 0;
}

/** A tracking link is shown only for https addresses (the API already refuses others; this guards the display). */
export function safeTrackingUrl(url: string | null | undefined): string | null {
  if (!url) return null;
  try {
    const parsed = new URL(url);
    return parsed.protocol === "https:" ? parsed.toString() : null;
  } catch {
    return null;
  }
}
