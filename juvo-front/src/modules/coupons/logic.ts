/**
 * Pure logic of the coupon screens (FR-C07, FR-Z04): code rules, money and number parsing, Dhaka time, the status of a coupon, what
 * deleting means, and the form <-> request mapping. No React here, so every rule is unit tested (tests/coupons.test.ts).
 *
 * The rules mirror the backend (CouponsService.Validate): a code is 3 to 40 letters, digits, hyphen or underscore and is stored upper
 * case; a percentage is above 0 and at most 100; a fixed amount is above 0; the minimum order, the cap and the usage limit are positive;
 * the end must be after the start; the limit cannot be lower than the uses already counted; the description has up to 200 characters.
 */
import { moneyToText, parseIntText, parseMoney } from "@/modules/delivery/logic";
import { dhakaLocalToIso, isoToDhakaLocal } from "@/modules/promotions/logic";

export { moneyToText };

export const CODE_MIN = 3;
export const CODE_MAX = 40;
export const MAX_AMOUNT = 10_000_000;
export const MAX_MINIMUM = 100_000_000;
export const MAX_LIMIT = 2_147_483_647;
export const MAX_DESCRIPTION = 200;

export type CouponKind = "Percent" | "Fixed";
export const COUPON_KINDS: readonly CouponKind[] = ["Percent", "Fixed"];

export type CouponStatus = "Active" | "Off" | "NotStarted" | "Expired" | "UsedUp";
export const COUPON_STATUSES: readonly CouponStatus[] = ["Active", "Off", "NotStarted", "Expired", "UsedUp"];

// ---- code ------------------------------------------------------------------------------------------------------

/** What a person typed as a code: upper case, spaces removed (the shop stores codes upper case and customers type them any way). */
export function normaliseCode(text: string): string {
  return text.replace(/\s+/g, "").toUpperCase();
}

export type CodeError = "required" | "length" | "chars";

export function validateCode(code: string): CodeError | null {
  const c = code.trim();
  if (c === "") return "required";
  if (!/^[A-Za-z0-9_-]+$/.test(c)) return "chars";
  if (c.length < CODE_MIN || c.length > CODE_MAX) return "length";
  return null;
}

const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0, O, 1, I: easy to read out over the phone

/** A readable random code such as `JV-K7M2QX`. Pure: the random source is a parameter (tests pass a fixed one). */
export function generateCode(random: () => number = Math.random, length = 6, prefix = "JV"): string {
  let out = "";
  for (let i = 0; i < length; i++) out += CODE_ALPHABET[Math.min(CODE_ALPHABET.length - 1, Math.floor(random() * CODE_ALPHABET.length))];
  return `${prefix}-${out}`;
}

// ---- status ----------------------------------------------------------------------------------------------------

export type CouponTiming = { isActive: boolean; startsAt: string | null; endsAt: string | null; usageLimit: number | string | null; usedCount: number | string };

const asNumber = (value: number | string | null | undefined): number => (value === null || value === undefined ? 0 : Number(value));

/** The same order the shop checks (Coupon.Check): switched off, not started, expired, used up; otherwise it works. */
export function couponStatus(c: CouponTiming, nowMs: number): CouponStatus {
  if (!c.isActive) return "Off";
  if (c.startsAt && nowMs < Date.parse(c.startsAt)) return "NotStarted";
  if (c.endsAt && nowMs > Date.parse(c.endsAt)) return "Expired";
  if (c.usageLimit !== null && c.usageLimit !== undefined && asNumber(c.usedCount) >= asNumber(c.usageLimit)) return "UsedUp";
  return "Active";
}

export type CouponFilters = { status?: string };

/** Search by code or description (any case), then the status filter. */
export function filterCoupons<T extends CouponTiming & { code: string; description: string | null }>(items: readonly T[], search: string, status: string | undefined, nowMs: number): T[] {
  const term = search.trim().toLowerCase();
  return items.filter((c) => {
    if (term && !c.code.toLowerCase().includes(term) && !(c.description ?? "").toLowerCase().includes(term)) return false;
    return !status || couponStatus(c, nowMs) === status;
  });
}

export function usageFigures(used: number | string, limit: number | string | null): { used: number; limit: number | null; left: number | null; percent: number | null } {
  const u = asNumber(used);
  if (limit === null || limit === undefined) return { used: u, limit: null, left: null, percent: null };
  const l = asNumber(limit);
  return { used: u, limit: l, left: Math.max(0, l - u), percent: l > 0 ? Math.min(100, Math.round((u / l) * 100)) : 100 };
}

// ---- rights and delete plan ------------------------------------------------------------------------------------

export type CouponRights = { view: boolean; create: boolean; edit: boolean; remove: boolean };

/**
 * What "Delete" does (CouponsService.DeleteAsync): a coupon that was never used and sits in no cart is deleted; one that was used (or is
 * in a cart) is refused with COUPON.INACTIVE and must be switched off, which keeps its record. `switchOff` needs the edit permission.
 */
export type DeletePlan = "delete" | "switchOff" | "alreadyOff" | "noEditRight";

export function deletePlan(c: { usedCount: number | string; isActive: boolean }, rights: Pick<CouponRights, "edit">): DeletePlan {
  if (asNumber(c.usedCount) === 0) return "delete";
  if (!c.isActive) return "alreadyOff";
  return rights.edit ? "switchOff" : "noEditRight";
}

// ---- form ------------------------------------------------------------------------------------------------------

export type CouponForm = {
  code: string;
  description: string;
  kind: CouponKind;
  value: string;
  minOrderValue: string;
  maxDiscount: string;
  startsAt: string; // YYYY-MM-DDTHH:mm, Dhaka time
  endsAt: string;
  usageLimit: string;
  isActive: boolean;
  stacksWithPromotions: boolean;
};

export type CouponLike = {
  code: string;
  description: string | null;
  kind: CouponKind;
  value: number | string;
  minOrderValue: number | string;
  maxDiscount: number | string | null;
  startsAt: string | null;
  endsAt: string | null;
  usageLimit: number | string | null;
  isActive: boolean;
  stacksWithPromotions?: boolean;
};

export function emptyCouponForm(): CouponForm {
  return { code: "", description: "", kind: "Percent", value: "", minOrderValue: "", maxDiscount: "", startsAt: "", endsAt: "", usageLimit: "", isActive: true, stacksWithPromotions: false };
}

export function couponToForm(c: CouponLike): CouponForm {
  return {
    code: c.code,
    description: c.description ?? "",
    kind: c.kind,
    value: moneyToText(c.value),
    minOrderValue: asNumber(c.minOrderValue) === 0 ? "" : moneyToText(c.minOrderValue),
    maxDiscount: c.maxDiscount === null || c.maxDiscount === undefined ? "" : moneyToText(c.maxDiscount),
    startsAt: isoToDhakaLocal(c.startsAt),
    endsAt: isoToDhakaLocal(c.endsAt),
    usageLimit: c.usageLimit === null || c.usageLimit === undefined ? "" : String(asNumber(c.usageLimit)),
    isActive: c.isActive,
    stacksWithPromotions: c.stacksWithPromotions ?? false,
  };
}

export type CouponErrorKey =
  | "codeRequired" | "codeLength" | "codeChars"
  | "valueRequired" | "valueInvalid" | "valueDecimals" | "valueNotPositive" | "valueTooBig" | "percentOver100"
  | "minInvalid" | "minDecimals" | "minNegative" | "minTooBig"
  | "capInvalid" | "capDecimals" | "capNotPositive" | "capTooBig"
  | "endBeforeStart"
  | "limitInvalid" | "limitNotPositive" | "limitTooBig" | "limitBelowUsed"
  | "descriptionTooLong";
export type CouponErrors = Partial<Record<"code" | "value" | "minOrderValue" | "maxDiscount" | "endsAt" | "usageLimit" | "description", CouponErrorKey>>;

/** The end must come after the start (the API refuses equal times). Empty on either side is fine. */
export function windowOrderedStrict(startsLocal: string, endsLocal: string): boolean {
  const s = dhakaLocalToIso(startsLocal);
  const e = dhakaLocalToIso(endsLocal);
  if (!s || !e) return true;
  return Date.parse(e) > Date.parse(s);
}

export function validateCoupon(form: CouponForm, opts: { isNew: boolean; usedCount?: number }): CouponErrors {
  const errors: CouponErrors = {};
  if (opts.isNew) {
    const code = validateCode(form.code);
    if (code) errors.code = code === "required" ? "codeRequired" : code === "length" ? "codeLength" : "codeChars";
  }

  const percent = form.kind === "Percent";
  const value = parseMoney(form.value, MAX_AMOUNT);
  if (!value.ok) {
    errors.value = ({ required: "valueRequired", invalid: "valueInvalid", negative: "valueNotPositive", decimals: "valueDecimals", tooBig: percent ? "percentOver100" : "valueTooBig" } as const)[value.error];
  } else if (value.value <= 0) {
    errors.value = "valueNotPositive";
  } else if (percent && value.value > 100) {
    errors.value = "percentOver100";
  }

  if (form.minOrderValue.trim() !== "") {
    const min = parseMoney(form.minOrderValue, MAX_MINIMUM);
    if (!min.ok) errors.minOrderValue = ({ required: "minInvalid", invalid: "minInvalid", negative: "minNegative", decimals: "minDecimals", tooBig: "minTooBig" } as const)[min.error];
  }

  if (percent && form.maxDiscount.trim() !== "") {
    const cap = parseMoney(form.maxDiscount, MAX_AMOUNT);
    if (!cap.ok) errors.maxDiscount = ({ required: "capInvalid", invalid: "capInvalid", negative: "capNotPositive", decimals: "capDecimals", tooBig: "capTooBig" } as const)[cap.error];
    else if (cap.value <= 0) errors.maxDiscount = "capNotPositive";
  }

  if (!windowOrderedStrict(form.startsAt, form.endsAt)) errors.endsAt = "endBeforeStart";

  if (form.usageLimit.trim() !== "") {
    const limit = parseIntText(form.usageLimit);
    if (limit === null) errors.usageLimit = "limitInvalid";
    else if (limit <= 0) errors.usageLimit = "limitNotPositive";
    else if (limit > MAX_LIMIT) errors.usageLimit = "limitTooBig";
    else if (opts.usedCount !== undefined && limit < opts.usedCount) errors.usageLimit = "limitBelowUsed";
  }

  if (form.description.trim().length > MAX_DESCRIPTION) errors.description = "descriptionTooLong";
  return errors;
}

export function hasCouponErrors(e: CouponErrors): boolean {
  return Object.keys(e).length > 0;
}

export type SaveCouponBody = {
  code: string;
  description: string | null;
  kind: CouponKind;
  value: number;
  minOrderValue: number;
  maxDiscount: number | null;
  startsAt: string | null;
  endsAt: string | null;
  usageLimit: number | null;
  isActive: boolean;
  stacksWithPromotions: boolean;
};

const money = (text: string): number => {
  const parsed = parseMoney(text, MAX_MINIMUM);
  return parsed.ok ? parsed.value : 0;
};

/** The request for a form that passed `validateCoupon`. Times typed as Dhaka time go out with the +06:00 offset; the cap only exists for a percentage. */
export function couponToRequest(form: CouponForm): SaveCouponBody {
  const percent = form.kind === "Percent";
  return {
    code: normaliseCode(form.code),
    description: form.description.trim() === "" ? null : form.description.trim(),
    kind: form.kind,
    value: money(form.value),
    minOrderValue: form.minOrderValue.trim() === "" ? 0 : money(form.minOrderValue),
    maxDiscount: percent && form.maxDiscount.trim() !== "" ? money(form.maxDiscount) : null,
    startsAt: dhakaLocalToIso(form.startsAt),
    endsAt: dhakaLocalToIso(form.endsAt),
    usageLimit: form.usageLimit.trim() === "" ? null : parseIntText(form.usageLimit),
    isActive: form.isActive,
    stacksWithPromotions: form.stacksWithPromotions,
  };
}

/** The same coupon with only the active flag changed (the API takes the whole coupon on every save). */
export function withActive(c: CouponLike, isActive: boolean): SaveCouponBody {
  return {
    code: c.code,
    description: c.description,
    kind: c.kind,
    value: asNumber(c.value),
    minOrderValue: asNumber(c.minOrderValue),
    maxDiscount: c.maxDiscount === null || c.maxDiscount === undefined ? null : asNumber(c.maxDiscount),
    startsAt: c.startsAt,
    endsAt: c.endsAt,
    usageLimit: c.usageLimit === null || c.usageLimit === undefined ? null : asNumber(c.usageLimit),
    isActive,
    stacksWithPromotions: c.stacksWithPromotions ?? false,
  };
}

/** The discount a coupon gives on a basket (same arithmetic as Coupon.DiscountFor), for the "worked example" line under the value field. */
export function exampleDiscount(form: Pick<CouponForm, "kind" | "value" | "maxDiscount">, basket: number): number | null {
  const value = parseMoney(form.value, MAX_AMOUNT);
  if (!value.ok || value.value <= 0) return null;
  let raw = form.kind === "Percent" ? (basket * value.value) / 100 : value.value;
  if (form.kind === "Percent" && form.maxDiscount.trim() !== "") {
    const cap = parseMoney(form.maxDiscount, MAX_AMOUNT);
    if (cap.ok && cap.value > 0) raw = Math.min(raw, cap.value);
  }
  return Math.round(Math.min(Math.max(raw, 0), basket) * 100) / 100;
}
