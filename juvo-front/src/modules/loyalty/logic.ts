import type { Schemas } from "@/lib/api/types";

/**
 * Pure rules of the rewards screens (module AB, docs/loyalty-api.md): tender maths and cap display, the settings form, ledger
 * formatting and code validation. No React and no network, so every rule can be tested on its own. The server always works out and
 * checks the real amounts; these helpers only decide what to suggest and show.
 */

export type LoyaltySettings = Schemas["LoyaltySettings"];
export type PointsRounding = Schemas["PointsRounding"];
export type PointsEntryKind = Schemas["PointsEntryKind"];
export type CreditEntryKind = Schemas["CreditEntryKind"];
export type TenderRequest = Schemas["TenderRequest"];
export type TenderQuote = Schemas["TenderQuoteDto"];

const num = (value: number | string | null | undefined): number => {
  if (value === null || value === undefined || value === "") return 0;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
};

/** Money rounded to whole paisa (two decimals), the way the server stores it. */
export const round2 = (value: number): number => Math.round((value + Number.EPSILON) * 100) / 100;

/* ---- Tender (points, store credit, gift card at checkout) ------------------------------------------------------------------- */

/** What the customer may use on an order of this total right now (GET /me/rewards/usable). */
export type Usable = {
  pointValue: number;
  minPointsToRedeem: number;
  maxTenderPercent: number;
  maxTenderAmount: number;
  maxPoints: number;
  maxCredit: number;
  pointsBalance: number;
  creditBalance: number;
};

export function toUsable(dto: Schemas["UsableOnCartDto"] | undefined): Usable | null {
  if (!dto) return null;
  return {
    pointValue: num(dto.pointValue),
    minPointsToRedeem: num(dto.minPointsToRedeem),
    maxTenderPercent: num(dto.maxTenderPercent),
    maxTenderAmount: num(dto.maxTenderAmount),
    maxPoints: Math.max(0, Math.floor(num(dto.maxPoints))),
    maxCredit: Math.max(0, num(dto.maxCredit)),
    pointsBalance: Math.max(0, Math.floor(num(dto.pointsBalance))),
    creditBalance: Math.max(0, num(dto.creditBalance)),
  };
}

/** BDT value of whole points. */
export const pointsToAmount = (points: number, pointValue: number): number => round2(Math.max(0, Math.floor(points)) * pointValue);

/** Whole number from what a person typed ("", "12", "12.7" gives 12); negative or unreadable gives 0. */
export function parseWhole(text: string): number {
  const n = Number(text.trim());
  return Number.isFinite(n) && n > 0 ? Math.floor(n) : 0;
}

/** Amount from what a person typed ("", "50", "50.5", "50,5"), two decimals at most; negative or unreadable gives 0. */
export function parseAmount(text: string): number {
  const n = Number(text.trim().replace(",", "."));
  return Number.isFinite(n) && n > 0 ? round2(n) : 0;
}

/** The most points to offer: not above what the cap and the balance allow, and 0 when that is below the minimum for one order. */
export function maxPointsToOffer(usable: Usable): number {
  return usable.maxPoints >= usable.minPointsToRedeem && usable.maxPoints > 0 ? usable.maxPoints : 0;
}

/** Store credit that still fits under the cap once the points are counted. */
export function creditRoom(usable: Usable, points: number): number {
  const left = round2(usable.maxTenderAmount - pointsToAmount(points, usable.pointValue));
  return Math.max(0, Math.min(usable.maxCredit, left));
}

/** A typed number of points made safe: whole, not above the maximum, and 0 below the order minimum. */
export function clampPoints(requested: number, usable: Usable): number {
  const whole = Math.max(0, Math.floor(requested));
  if (whole === 0) return 0;
  const capped = Math.min(whole, usable.maxPoints);
  return capped < usable.minPointsToRedeem ? 0 : capped;
}

/** The sentence parts of "points and store credit pay up to X% of the order (up to BDT Y)". */
export function capSummary(usable: Usable): { percent: number; amount: number } {
  return { percent: usable.maxTenderPercent, amount: usable.maxTenderAmount };
}

/** What a person typed in the tender form. */
export type TenderDraft = { points: string; credit: string; giftCardCode: string };

export const emptyTenderDraft: TenderDraft = { points: "", credit: "", giftCardCode: "" };

/** The request body for the quote and the checkout, or null when nothing is offered. */
export function toTenderRequest(draft: TenderDraft): TenderRequest | null {
  const points = parseWhole(draft.points);
  const credit = parseAmount(draft.credit);
  const code = normalizeCode(draft.giftCardCode);
  if (points === 0 && credit === 0 && code === "") return null;
  return { points, credit, giftCardCode: code === "" ? null : code };
}

export type TenderRow = { key: "points" | "credit" | "giftCard"; amount: number; points?: number };

/** The lines of the plan the quote returned, only the ones that are used. */
export function tenderRows(plan: TenderQuote | null | undefined): TenderRow[] {
  if (!plan) return [];
  const rows: TenderRow[] = [];
  if (num(plan.pointsAmount) > 0) rows.push({ key: "points", amount: num(plan.pointsAmount), points: num(plan.pointsUsed) });
  if (num(plan.creditUsed) > 0) rows.push({ key: "credit", amount: num(plan.creditUsed) });
  if (num(plan.giftCardUsed) > 0) rows.push({ key: "giftCard", amount: num(plan.giftCardUsed) });
  return rows;
}

/** True when the server refused the tender (the quote then answers canPlace false and names the problem). */
export const hasTenderProblem = (plan: TenderQuote | null | undefined): boolean => !!plan?.problem;

/** What is still to pay after the tender, never below zero and never above the total. */
export function payableAfterTender(total: number, tenderTotal: number): number {
  return Math.min(total, Math.max(0, round2(total - tenderTotal)));
}

/** True when a signed-in customer has anything to spend (points or credit), so the section is worth showing. */
export const hasSpendable = (plan: TenderQuote | null | undefined): boolean => !!plan && (num(plan.pointsBalance) > 0 || num(plan.creditBalance) > 0);

/* ---- Codes -------------------------------------------------------------------------------------------------------------------- */

/** The letters and digits the server uses for referral and gift card codes (no look-alikes). */
export const CODE_ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
export const REFERRAL_CODE_LENGTH = 8;
export const GIFT_CARD_CODE_LENGTH = 16;

/** Upper case, without spaces and dashes (customers type codes in any case, with or without dashes). */
export const normalizeCode = (input: string): string => input.toUpperCase().replace(/[\s-]+/g, "");

const onlyAlphabet = (code: string): boolean => [...code].every((c) => CODE_ALPHABET.includes(c));

export const isValidReferralCode = (input: string): boolean => {
  const code = normalizeCode(input);
  return code.length === REFERRAL_CODE_LENGTH && onlyAlphabet(code);
};

export const isValidGiftCardCode = (input: string): boolean => {
  const code = normalizeCode(input);
  return code.length === GIFT_CARD_CODE_LENGTH && onlyAlphabet(code);
};

/** A gift card code in groups of four ("ABCD-EFGH-JKLM-NPQR"). */
export const formatGiftCardCode = (input: string): string => normalizeCode(input).replace(/(.{4})(?=.)/g, "$1-");

/* ---- Settings form (FR-AB01) -------------------------------------------------------------------------------------------------- */

/** One field of the settings form: its key, range and whether it takes whole numbers. */
export type SettingField = { key: Exclude<keyof LoyaltySettings, "enabled" | "earnRounding">; min: number; max: number; whole: boolean; decimals?: number };

/** Ranges as in docs/loyalty-api.md section 1. giftCardMinAmount and giftCardMaxAmount have a cross check below. */
export const SETTING_FIELDS: SettingField[] = [
  { key: "earnRatePerBdt", min: 0, max: 100, whole: false, decimals: 4 },
  { key: "pointValue", min: 0.01, max: 1000, whole: false, decimals: 2 },
  { key: "minPointsToRedeem", min: 0, max: 1_000_000, whole: true },
  { key: "maxTenderPercent", min: 0, max: 100, whole: true },
  { key: "pointsExpiryMonths", min: 0, max: 120, whole: true },
  { key: "expiryWarningDays", min: 0, max: 180, whole: true },
  { key: "referralRewardReferrer", min: 0, max: 100_000, whole: false, decimals: 2 },
  { key: "referralRewardReferee", min: 0, max: 100_000, whole: false, decimals: 2 },
  { key: "referralMaxRewardsPerReferrer", min: 0, max: 10_000, whole: true },
  { key: "giftCardMinAmount", min: 0.01, max: Number.MAX_SAFE_INTEGER, whole: false, decimals: 2 },
  { key: "giftCardMaxAmount", min: 0.01, max: Number.MAX_SAFE_INTEGER, whole: false, decimals: 2 },
  { key: "giftCardDefaultValidityMonths", min: 0, max: 120, whole: true },
];

export type SettingsForm = { enabled: boolean; earnRounding: PointsRounding } & Record<SettingField["key"], string>;

export const ROUNDINGS: PointsRounding[] = ["Down", "Nearest", "Up"];

/** The API's numbers as text for the inputs (no trailing zeros). */
export function toSettingsForm(settings: LoyaltySettings): SettingsForm {
  const form = { enabled: settings.enabled, earnRounding: settings.earnRounding } as SettingsForm;
  for (const f of SETTING_FIELDS) form[f.key] = String(num(settings[f.key]));
  return form;
}

export type SettingsErrors = Partial<Record<SettingField["key"], "required" | "number" | "whole" | "range" | "giftOrder">>;

/** Checks every field against the ranges of the API (the API checks again). Empty result = valid. */
export function validateSettingsForm(form: SettingsForm): SettingsErrors {
  const errors: SettingsErrors = {};
  for (const f of SETTING_FIELDS) {
    const raw = form[f.key].trim().replace(",", ".");
    if (raw === "") {
      errors[f.key] = "required";
      continue;
    }
    const n = Number(raw);
    if (!Number.isFinite(n)) errors[f.key] = "number";
    else if (f.whole && !Number.isInteger(n)) errors[f.key] = "whole";
    else if (n < f.min || n > f.max) errors[f.key] = "range";
  }
  if (!errors.giftCardMinAmount && !errors.giftCardMaxAmount && Number(form.giftCardMaxAmount) < Number(form.giftCardMinAmount)) errors.giftCardMaxAmount = "giftOrder";
  return errors;
}

/** Only what changed, as the PUT body (the API changes only the fields it receives; the rest stay null). */
export function toSettingsPatch(original: LoyaltySettings, form: SettingsForm): Schemas["UpdateLoyaltySettingsRequest"] {
  const patch: Schemas["UpdateLoyaltySettingsRequest"] = {
    enabled: form.enabled !== original.enabled ? form.enabled : null,
    earnRounding: form.earnRounding !== original.earnRounding ? form.earnRounding : null,
    earnRatePerBdt: null,
    pointValue: null,
    minPointsToRedeem: null,
    maxTenderPercent: null,
    pointsExpiryMonths: null,
    expiryWarningDays: null,
    referralRewardReferrer: null,
    referralRewardReferee: null,
    referralMaxRewardsPerReferrer: null,
    giftCardMinAmount: null,
    giftCardMaxAmount: null,
    giftCardDefaultValidityMonths: null,
  };
  for (const f of SETTING_FIELDS) {
    const next = Number(form[f.key].trim().replace(",", "."));
    if (next !== num(original[f.key])) (patch as Record<string, unknown>)[f.key] = next;
  }
  return patch;
}

/** True when the form differs from the saved settings. */
export const isSettingsChanged = (original: LoyaltySettings, form: SettingsForm): boolean => {
  const p = toSettingsPatch(original, form);
  return Object.values(p).some((v) => v !== null);
};

/** What the on/off switch means in plain words: the message key to show. */
export const enabledConsequence = (enabled: boolean): "on" | "off" => (enabled ? "on" : "off");

/** "1 point per BDT 100" from the earn rate (0.01 gives 100 BDT per point; 2 gives 2 points per BDT). */
export function earnExample(rate: number): { points: number; perBdt: number } | null {
  if (!(rate > 0)) return null;
  if (rate >= 1) return { points: round2(rate), perBdt: 1 };
  return { points: 1, perBdt: round2(1 / rate) };
}

/* ---- Ledgers (FR-AB04) -------------------------------------------------------------------------------------------------------- */

/** A signed number as text with a real minus sign: +50, -20, 0. */
export function signed(value: number, format: (n: number) => string = String): string {
  if (value > 0) return `+${format(value)}`;
  if (value < 0) return `−${format(Math.abs(value))}`;
  return format(0);
}

export type LedgerTone = "success" | "danger" | "neutral";

/** Colour family of a ledger line from the sign of its amount (the sign is always in the text too). */
export const ledgerTone = (amount: number): LedgerTone => (amount > 0 ? "success" : amount < 0 ? "danger" : "neutral");

export const POINTS_KINDS: PointsEntryKind[] = ["Earn", "Redeem", "Release", "Reverse", "Expire", "Adjust", "ReferralReward"];
export const CREDIT_KINDS: CreditEntryKind[] = ["GiftCardRedeem", "Spend", "Release", "Refund", "Adjust", "ReferralReward", "ReferralClawback"];

/* ---- Staff screens ------------------------------------------------------------------------------------------------------------ */

export type GiftCardStatus = "active" | "used" | "expired" | "deactivated";
export const GIFT_CARD_STATUSES: GiftCardStatus[] = ["active", "used", "expired", "deactivated"];

/** The API sends the card status as text in the case it likes; this gives one of the four known values or null. */
export function giftCardStatus(value: string): GiftCardStatus | null {
  const v = value.trim().toLowerCase();
  return (GIFT_CARD_STATUSES as string[]).includes(v) ? (v as GiftCardStatus) : null;
}

export const REFERRAL_STATUSES = ["Pending", "Rewarded", "Rejected", "Reversed"] as const;

/** Result of reading the manual adjustment form: whole points and/or BDT credit, both signed, plus a reason. */
export type AdjustDraft = { points: string; credit: string; reason: string };
export type AdjustErrors = Partial<Record<"points" | "credit" | "reason" | "none", "number" | "whole" | "required">>;

const signedNumber = (text: string): number | null => {
  const raw = text.trim().replace(",", ".");
  if (raw === "") return 0;
  const n = Number(raw);
  return Number.isFinite(n) ? n : null;
};

export function validateAdjust(draft: AdjustDraft): AdjustErrors {
  const errors: AdjustErrors = {};
  const points = signedNumber(draft.points);
  const credit = signedNumber(draft.credit);
  if (points === null) errors.points = "number";
  else if (!Number.isInteger(points)) errors.points = "whole";
  if (credit === null) errors.credit = "number";
  if (!errors.points && !errors.credit && (points ?? 0) === 0 && (credit ?? 0) === 0) errors.none = "required";
  if (draft.reason.trim() === "") errors.reason = "required";
  return errors;
}

/** The request body of an adjustment: only the non-zero parts are sent. */
export function toAdjustBody(draft: AdjustDraft): Schemas["AdjustRewardsRequest"] {
  const points = signedNumber(draft.points) ?? 0;
  const credit = signedNumber(draft.credit) ?? 0;
  return { points: points !== 0 ? points : null, credit: credit !== 0 ? round2(credit) : null, reason: draft.reason.trim() };
}

export type GiftCardDraft = { amount: string; expiresOn: string; note: string; recipientMobile: string; recipientEmail: string; send: boolean };
export type GiftCardErrors = Partial<Record<"amount" | "expiresOn" | "recipient", "required" | "number" | "range" | "past" | "needsRecipient">>;

/** Checks the create form against the limits in the settings. `today` is "YYYY-MM-DD". */
export function validateGiftCard(draft: GiftCardDraft, limits: { min: number; max: number } | null, today: string): GiftCardErrors {
  const errors: GiftCardErrors = {};
  const raw = draft.amount.trim().replace(",", ".");
  const amount = Number(raw);
  if (raw === "") errors.amount = "required";
  else if (!Number.isFinite(amount) || amount <= 0) errors.amount = "number";
  else if (limits && (amount < limits.min || amount > limits.max)) errors.amount = "range";
  if (draft.expiresOn && draft.expiresOn < today) errors.expiresOn = "past";
  if (draft.send && draft.recipientMobile.trim() === "" && draft.recipientEmail.trim() === "") errors.recipient = "needsRecipient";
  return errors;
}

/** The request body of a new gift card. The expiry is the end of the chosen day in local time; empty uses the default validity. */
export function toGiftCardBody(draft: GiftCardDraft): Schemas["CreateGiftCardRequest"] {
  const expires = draft.expiresOn ? new Date(`${draft.expiresOn}T23:59:59`).toISOString() : null;
  return {
    amount: round2(Number(draft.amount.trim().replace(",", "."))),
    expiresAt: expires,
    note: draft.note.trim() || null,
    recipientMobile: draft.recipientMobile.trim() || null,
    recipientEmail: draft.recipientEmail.trim() || null,
    sendToRecipient: draft.send,
  };
}
