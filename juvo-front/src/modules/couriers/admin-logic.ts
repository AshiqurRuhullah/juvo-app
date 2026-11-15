/**
 * Pure logic of the courier and parcel admin screens (forms, validation, label ids). No state and no calls; unit tested in
 * tests/couriers-admin.test.ts. The rules mirror docs/couriers-api.md section 3; the API stays the judge.
 */
import type { Courier, SaveCourier } from "./api";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** The ids of `?ids=a,b,c` (or repeated `ids`): only well-formed ids, no repeats, at most `max` (labels: 50). */
export function parseIds(raw: string | string[] | undefined, max = 50): string[] {
  const text = Array.isArray(raw) ? raw.join(",") : (raw ?? "");
  const seen = new Set<string>();
  for (const part of text.split(",")) {
    const id = part.trim().toLowerCase();
    if (UUID.test(id)) seen.add(id);
    if (seen.size >= max) break;
  }
  return [...seen];
}

/** Settings and the secrets rule: the API refuses a key that looks like a secret, so the form says so before sending. */
const SECRET_WORDS = ["secret", "password", "token", "apikey", "private", "credential"];
export function looksLikeSecret(key: string): boolean {
  const flat = key.toLowerCase().replace(/[^a-z0-9]/g, "");
  return SECRET_WORDS.some((word) => flat.includes(word));
}

export type SettingRow = { key: string; value: string };
export const MAX_SETTINGS = 20;

export function settingsToRows(settings: Record<string, string> | null | undefined): SettingRow[] {
  return Object.entries(settings ?? {}).map(([key, value]) => ({ key, value }));
}

export function rowsToSettings(rows: readonly SettingRow[]): Record<string, string> {
  const result: Record<string, string> = {};
  for (const row of rows) {
    const key = row.key.trim();
    if (key) result[key] = row.value.trim();
  }
  return result;
}

/** Everything the form edits, as text (inputs are strings; numbers are parsed when saving). */
export type CourierFormValues = {
  code: string;
  name: string;
  provider: string;
  isEnabled: boolean;
  priority: string;
  codSupported: boolean;
  supportsInternational: boolean;
  minWeightGrams: string;
  maxWeightGrams: string;
  supportedZoneCodes: string[];
  chargeOverride: string;
  baseRate: string;
  perExtraKgRate: string;
  baseRateUpToGrams: string;
  trackingUrlTemplate: string;
  notes: string;
  settings: SettingRow[];
};

export type CourierFormErrors = Partial<Record<"code" | "name" | "provider" | "priority" | "minWeightGrams" | "maxWeightGrams" | "chargeOverride" | "baseRate" | "perExtraKgRate" | "baseRateUpToGrams" | "trackingUrlTemplate" | "settings", string>>;

const num = (v: unknown) => (v === null || v === undefined ? "" : String(v));

export function emptyCourierForm(provider = "SANDBOX"): CourierFormValues {
  return {
    code: "",
    name: "",
    provider,
    isEnabled: false,
    priority: "100",
    codSupported: true,
    supportsInternational: false,
    minWeightGrams: "",
    maxWeightGrams: "",
    supportedZoneCodes: [],
    chargeOverride: "",
    baseRate: "0",
    perExtraKgRate: "0",
    baseRateUpToGrams: "1000",
    trackingUrlTemplate: "",
    notes: "",
    settings: [],
  };
}

export function courierToForm(c: Courier): CourierFormValues {
  return {
    code: c.code,
    name: c.name,
    provider: c.provider,
    isEnabled: c.isEnabled,
    priority: num(c.priority),
    codSupported: c.codSupported,
    supportsInternational: c.supportsInternational,
    minWeightGrams: num(c.minWeightGrams),
    maxWeightGrams: num(c.maxWeightGrams),
    supportedZoneCodes: [...c.supportedZoneCodes],
    chargeOverride: num(c.chargeOverride),
    baseRate: num(c.baseRate),
    perExtraKgRate: num(c.perExtraKgRate),
    baseRateUpToGrams: num(c.baseRateUpToGrams),
    trackingUrlTemplate: c.trackingUrlTemplate ?? "",
    notes: c.notes ?? "",
    settings: settingsToRows(c.settings),
  };
}

export const isValidCourierCode = (code: string) => /^[A-Z0-9_-]{2,30}$/.test(code);

/** A tracking template must be https and contain the {consignment} placeholder (empty is fine). */
export function isValidTrackingTemplate(value: string): boolean {
  const text = value.trim();
  if (!text) return true;
  return /^https:\/\//i.test(text) && text.includes("{consignment}") && text.length <= 300;
}

const isInt = (v: string) => /^\d+$/.test(v.trim());
const isMoney = (v: string) => /^\d+(\.\d{1,2})?$/.test(v.trim());

/** Messages are keys of `couriers.form.errors.*`; an empty object means the form can be sent. */
export function validateCourierForm(v: CourierFormValues, isNew: boolean): CourierFormErrors {
  const e: CourierFormErrors = {};
  if (isNew && !isValidCourierCode(v.code.trim().toUpperCase())) e.code = "code";
  if (!v.name.trim() || v.name.trim().length > 100) e.name = "name";
  if (!v.provider) e.provider = "provider";
  if (!isInt(v.priority) || Number(v.priority) < 1 || Number(v.priority) > 1000) e.priority = "priority";
  if (v.minWeightGrams.trim() && !isInt(v.minWeightGrams)) e.minWeightGrams = "weight";
  if (v.maxWeightGrams.trim() && !isInt(v.maxWeightGrams)) e.maxWeightGrams = "weight";
  if (!e.minWeightGrams && !e.maxWeightGrams && v.minWeightGrams.trim() && v.maxWeightGrams.trim() && Number(v.minWeightGrams) > Number(v.maxWeightGrams)) e.maxWeightGrams = "weightRange";
  if (v.chargeOverride.trim() && !isMoney(v.chargeOverride)) e.chargeOverride = "money";
  if (!isMoney(v.baseRate)) e.baseRate = "money";
  if (!isMoney(v.perExtraKgRate)) e.perExtraKgRate = "money";
  if (!isInt(v.baseRateUpToGrams) || Number(v.baseRateUpToGrams) < 1) e.baseRateUpToGrams = "weight";
  if (!isValidTrackingTemplate(v.trackingUrlTemplate)) e.trackingUrlTemplate = "tracking";
  const keys = v.settings.map((r) => r.key.trim()).filter(Boolean);
  if (keys.some(looksLikeSecret)) e.settings = "secret";
  else if (new Set(keys.map((k) => k.toLowerCase())).size !== keys.length) e.settings = "duplicate";
  else if (keys.length > MAX_SETTINGS) e.settings = "tooMany";
  return e;
}

const orNull = (v: string) => (v.trim() === "" ? null : Number(v));

/** The request body; `code` travels separately (only on create). */
export function formToRequest(v: CourierFormValues): SaveCourier {
  return {
    code: null,
    name: v.name.trim(),
    provider: v.provider,
    isEnabled: v.isEnabled,
    priority: Number(v.priority),
    codSupported: v.codSupported,
    supportsInternational: v.supportsInternational,
    minWeightGrams: orNull(v.minWeightGrams),
    maxWeightGrams: orNull(v.maxWeightGrams),
    supportedZoneCodes: v.supportedZoneCodes,
    chargeOverride: orNull(v.chargeOverride),
    baseRate: Number(v.baseRate),
    perExtraKgRate: Number(v.perExtraKgRate),
    baseRateUpToGrams: Number(v.baseRateUpToGrams),
    trackingUrlTemplate: v.trackingUrlTemplate.trim() || null,
    notes: v.notes.trim() || null,
    settings: rowsToSettings(v.settings),
  };
}

/** Raw statuses the sandbox simulator accepts (docs/couriers-api.md section 4), in journey order. */
export const ADVANCE_RAW = ["picked_up", "in_transit", "at_hub", "out_for_delivery", "delivered", "delivery_failed", "returned"] as const;

/** Maps a shipment status (next step) to the simulator's raw status. */
export function rawForStatus(status: string): string | null {
  switch (status) {
    case "PickedUp":
      return "picked_up";
    case "InTransit":
      return "in_transit";
    case "OutForDelivery":
      return "out_for_delivery";
    case "Delivered":
      return "delivered";
    case "DeliveryFailed":
      return "delivery_failed";
    case "Returned":
      return "returned";
    default:
      return null;
  }
}

/** The step the simulator takes when no status is chosen (the API's rule): the next step of the normal journey among the allowed ones. */
export function defaultNext(nextStatuses: readonly string[]): string | null {
  return ["PickedUp", "InTransit", "OutForDelivery", "Delivered"].find((s) => nextStatuses.includes(s)) ?? null;
}

/** Positive whole number within a range, or null for empty input; `undefined` when the text is not valid. */
export function parseOptionalInt(text: string, min: number, max: number): number | null | undefined {
  const value = text.trim();
  if (!value) return null;
  if (!/^\d+$/.test(value)) return undefined;
  const n = Number(value);
  return n >= min && n <= max ? n : undefined;
}

/** Counts of the active filters of the parcel list (for the "clear" button and the summary). */
export function activeFilterCount(filters: Record<string, string | undefined>, flags: { codOnly: boolean; open: boolean }): number {
  return Object.values(filters).filter(Boolean).length + (flags.codOnly ? 1 : 0) + (flags.open ? 1 : 0);
}

/** The label page link for the given parcels (the bulk "print labels" action). */
export function labelsHref(ids: readonly string[]): string {
  return `/admin/shipments/labels?ids=${ids.slice(0, 50).join(",")}`;
}
