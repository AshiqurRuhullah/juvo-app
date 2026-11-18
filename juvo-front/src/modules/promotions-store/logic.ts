/**
 * Pure logic of the shop's promotion display (FR-Z01..Z03): countdown maths, the price lines of a cart or quote, the coupon outcome
 * and the "why this price" sentence. No React and no network here, so every rule is unit-tested (tests/promotions-store.test.ts).
 */

type Num = number | string | null | undefined;
type Locale = string;

const num = (value: Num): number => {
  if (value === null || value === undefined || value === "") return 0;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
};

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/* ---- Countdown ------------------------------------------------------------------------------------------------------------- */

export type CountdownParts = { ended: boolean; totalMs: number; days: number; hours: number; minutes: number; seconds: number };

/** What is left until `endsAt`, split into days, hours, minutes and seconds. Null when the offer has no end time or it is not a date. */
export function countdownParts(endsAt: string | number | Date | null | undefined, now: number): CountdownParts | null {
  if (endsAt === null || endsAt === undefined || endsAt === "") return null;
  const end = endsAt instanceof Date ? endsAt.getTime() : typeof endsAt === "number" ? endsAt : Date.parse(endsAt);
  if (!Number.isFinite(end)) return null;
  const left = end - now;
  // The API prices a flash sale up to and including its last second, so the countdown only ends after 0.
  if (left < 0) return { ended: true, totalMs: 0, days: 0, hours: 0, minutes: 0, seconds: 0 };
  const total = Math.floor(left / 1000);
  return {
    ended: false,
    totalMs: left,
    days: Math.floor(total / 86_400),
    hours: Math.floor((total % 86_400) / 3600),
    minutes: Math.floor((total % 3600) / 60),
    seconds: total % 60,
  };
}

/** Seconds are only worth a tick each second inside the last hour; before that one tick a minute is enough (no needless work). */
export function tickStepMs(remainingMs: number): number {
  return remainingMs <= HOUR + MINUTE ? 1000 : MINUTE;
}

export type CountdownSegment = { unit: "d" | "h" | "m" | "s"; value: number };

/**
 * The segments to print: days only when there is at least one, seconds only inside the last hour. The shape of the list changes
 * rarely (at 1 day, at 1 hour), and every number is printed with a fixed width by the component, so the line does not jump.
 */
export function countdownSegments(parts: CountdownParts): CountdownSegment[] {
  if (parts.ended) return [];
  const out: CountdownSegment[] = [];
  if (parts.days > 0) out.push({ unit: "d", value: parts.days });
  out.push({ unit: "h", value: parts.hours });
  out.push({ unit: "m", value: parts.minutes });
  if (parts.totalMs <= HOUR + MINUTE) out.push({ unit: "s", value: parts.seconds });
  return out;
}

/** "02:14:09" style for the plain-text fallback and for tests. */
export function clockText(parts: CountdownParts): string {
  const two = (n: number) => String(n).padStart(2, "0");
  const head = parts.days > 0 ? `${parts.days}d ` : "";
  return `${head}${two(parts.hours)}:${two(parts.minutes)}${parts.totalMs <= HOUR + MINUTE ? `:${two(parts.seconds)}` : ""}`;
}

export const COUNTDOWN_CONSTANTS = { MINUTE, HOUR, DAY } as const;

/* ---- Price on a card or product page --------------------------------------------------------------------------------------- */

export type PriceView = { price: number; regular: number; saving: number; percent: number; onSale: boolean };

/** The numbers a card or the product page prints: the price now, the struck-through list price, the saving and the percentage off. */
export function priceView(price: Num, regular: Num): PriceView {
  const now = num(price);
  const list = num(regular);
  const onSale = list > 0 && now < list;
  const saving = onSale ? Math.round((list - now) * 100) / 100 : 0;
  return { price: now, regular: list, saving, percent: onSale ? Math.round((saving / list) * 100) : 0, onSale };
}

export type PromotionInfoLike = { labelEn: string; labelBn: string; endsAt?: string | null; type?: string } | null | undefined;

/** The short customer label of a promotion in the page language. */
export function promotionLabel(promotion: PromotionInfoLike, locale: Locale): string | null {
  if (!promotion) return null;
  const label = locale === "bn" ? promotion.labelBn || promotion.labelEn : promotion.labelEn || promotion.labelBn;
  return label?.trim() ? label : null;
}

/* ---- Cart and checkout price lines ----------------------------------------------------------------------------------------- */

export type ReasonLike = { sourceType: string; labelEn: string; labelBn: string; amount: Num };
export type ExplanationLike = { listTotal?: Num; finalTotal?: Num; savings?: Num; reasons?: ReasonLike[] | null; notesEn?: string[] | null; notesBn?: string[] | null } | null | undefined;
export type CouponLike = { code: string; discount: Num; applied?: boolean | null; notAppliedReasonEn?: string | null; notAppliedReasonBn?: string | null; problem?: string | null } | null | undefined;

export type SavingRow = { key: string; kind: "list" | "reason"; label: string; value: number; sourceType?: string };

const pickLabel = (locale: Locale, en: string, bn: string) => (locale === "bn" && bn ? bn : en || bn);

/**
 * The lines that explain the money between the list price and the goods total: the list price first, then what each sale price,
 * promotion, bundle or buy-X-get-Y took off. The coupon has its own line elsewhere (it keeps its own row and test ids). When the
 * answer carries no explanation the saving is one "Offers" line, so the rows still add up. Nothing is returned when nothing was saved.
 */
export function savingRows(input: { explanation?: ExplanationLike; listSubtotal: Num; subtotal: Num; promotionDiscount: Num }, locale: Locale, offersLabel: string): SavingRow[] {
  const list = num(input.listSubtotal);
  const lineSaving = Math.max(0, list - num(input.subtotal));
  const orderSaving = num(input.promotionDiscount);
  const total = round2(lineSaving + orderSaving);
  if (list <= 0 || total <= 0) return [];
  const reasons = (input.explanation?.reasons ?? []).filter((r) => r.sourceType !== "Coupon" && num(r.amount) > 0);
  const rows: SavingRow[] = [{ key: "list", kind: "list", label: "", value: list }];
  const explained = round2(reasons.reduce((sum, r) => sum + num(r.amount), 0));
  if (reasons.length > 0 && Math.abs(explained - total) < 0.011) {
    reasons.forEach((r, index) => rows.push({ key: `promo-${index}`, kind: "reason", label: pickLabel(locale, r.labelEn, r.labelBn), value: num(r.amount), sourceType: r.sourceType }));
  } else {
    rows.push({ key: "promo-0", kind: "reason", label: offersLabel, value: total });
  }
  return rows;
}

/** True when the cart saved anything at all (sale price, promotion, bundle ...), so the breakdown is worth showing. */
export function hasSavings(input: { listSubtotal: Num; subtotal: Num; promotionDiscount: Num }): boolean {
  return num(input.listSubtotal) > 0 && round2(Math.max(0, num(input.listSubtotal) - num(input.subtotal)) + num(input.promotionDiscount)) > 0;
}

export const round2 = (n: number) => Math.round(n * 100) / 100;

export type CouponView =
  | { state: "none" }
  | { state: "applied"; code: string; discount: number }
  | { state: "lost"; code: string; reason: string | null }
  | { state: "invalid"; code: string };

/** What happened to the coupon: used, kept on the cart but beaten by better promotions (decision P4), or no longer valid. */
export function couponView(coupon: CouponLike, locale: Locale): CouponView {
  if (!coupon) return { state: "none" };
  if (coupon.problem) return { state: "invalid", code: coupon.code };
  if (coupon.applied === false) {
    const reason = locale === "bn" ? coupon.notAppliedReasonBn || coupon.notAppliedReasonEn : coupon.notAppliedReasonEn || coupon.notAppliedReasonBn;
    return { state: "lost", code: coupon.code, reason: reason?.trim() ? reason : null };
  }
  return { state: "applied", code: coupon.code, discount: num(coupon.discount) };
}

/** The notes of the answer (why a coupon was or was not used, free delivery) in the page language. */
export function explanationNotes(explanation: ExplanationLike, locale: Locale): string[] {
  const list = (locale === "bn" ? explanation?.notesBn : explanation?.notesEn) ?? [];
  const fallback = (locale === "bn" ? explanation?.notesEn : explanation?.notesBn) ?? [];
  return (list.length > 0 ? list : fallback).filter((n) => n && n.trim());
}

export type WhyThisPrice = { labels: string[]; saving: number; notes: string[] };

/** The facts for the one-line "why this price": each source's label, the total saving and the notes the answer carries. */
export function whyThisPrice(explanation: ExplanationLike, locale: Locale): WhyThisPrice | null {
  if (!explanation) return null;
  const labels = (explanation.reasons ?? []).filter((r) => num(r.amount) > 0).map((r) => pickLabel(locale, r.labelEn, r.labelBn));
  const saving = num(explanation.savings);
  const notes = explanationNotes(explanation, locale);
  if (labels.length === 0 && notes.length === 0) return null;
  return { labels: [...new Set(labels)], saving, notes };
}

export type OfferLike = { qualifies: boolean; amountToQualify: Num; labelEn: string; labelBn: string; zoneCodes?: string[] | null };

/** The free-delivery message for the cart: already free, or how much more is needed (the best open offer). */
export function freeDeliveryView(offers: OfferLike[] | null | undefined, locale: Locale): { state: "free"; label: string } | { state: "more"; label: string; amount: number; zoneLimited: boolean } | null {
  const list = offers ?? [];
  const won = list.find((o) => o.qualifies);
  if (won) return { state: "free", label: pickLabel(locale, won.labelEn, won.labelBn) };
  const open = list.filter((o) => num(o.amountToQualify) > 0).sort((a, b) => num(a.amountToQualify) - num(b.amountToQualify))[0];
  if (!open) return null;
  return { state: "more", label: pickLabel(locale, open.labelEn, open.labelBn), amount: num(open.amountToQualify), zoneLimited: (open.zoneCodes ?? []).length > 0 };
}

/** Per line: the list price struck through and the label of the promotion behind the unit price, or nothing when the price is the list price. */
export function lineOffer(line: { unitPrice: Num; listPrice: Num; orderDiscount?: Num; promotionLabelEn?: string | null; promotionLabelBn?: string | null }, locale: Locale): { list: number; saving: number; label: string | null } | null {
  const list = num(line.listPrice);
  const unit = num(line.unitPrice);
  const order = num(line.orderDiscount);
  if (list <= 0 || (unit >= list && order <= 0)) return null;
  const label = locale === "bn" ? line.promotionLabelBn || line.promotionLabelEn : line.promotionLabelEn || line.promotionLabelBn;
  return { list, saving: round2(Math.max(0, list - unit)), label: label?.trim() ? label : null };
}
