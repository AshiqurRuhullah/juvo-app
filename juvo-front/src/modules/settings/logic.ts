/**
 * Pure logic of the settings centre (FR-AF01, FR-AF08 in part): draft <-> value conversion per type, validation that mirrors the
 * backend's SettingDefinition (type, range, length, allowed choices), dirty detection, grouping and search, the "dangerous switch"
 * map and the history line. No React here, so every rule is unit tested (tests/settings.test.ts). The API stays the judge: the screen
 * only shows its own messages early; whatever the API says is shown as it is.
 *
 * The API gives no label, only a key and an English and a Bengali description; the description is the visible name and the key is
 * shown small under it. The API does not tell the text format (time, e-mail, address) either, so formats are checked by the server only.
 */

export type SettingType = "bool" | "whole" | "number" | "text" | "choice";
export const SETTING_TYPES: readonly SettingType[] = ["bool", "whole", "number", "text", "choice"];

/** Group order the API sends them in; used only to sort unknown groups after the known ones. */
export const GROUP_ORDER = ["features", "loyalty", "payments", "orders", "inventory", "returns", "business", "notifications", "marketing", "operations", "reports", "locale", "security"] as const;

export type SettingLike = {
  key: string;
  group: string;
  type: string;
  value: unknown;
  default: unknown;
  source: string;
  isSet: boolean;
  descriptionEn: string;
  descriptionBn: string;
  allowedValues: string[] | null;
  min: number | string | null;
  max: number | string | null;
  maxLength: number | string | null;
  requiresRestart: boolean;
  sensitive: boolean;
  readOnly: boolean;
  canEdit: boolean;
  editPermission: string | null;
  version: string | null;
};

export type GroupLike<S extends SettingLike = SettingLike> = { group: string; settings: S[] };

export const asType = (type: string): SettingType => ((SETTING_TYPES as readonly string[]).includes(type) ? (type as SettingType) : "text");

const num = (value: number | string | null | undefined): number | null => {
  if (value === null || value === undefined || value === "") return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
};

export const minOf = (s: Pick<SettingLike, "min">) => num(s.min);
export const maxOf = (s: Pick<SettingLike, "max">) => num(s.max);
export const maxLengthOf = (s: Pick<SettingLike, "maxLength">) => num(s.maxLength);

// ---- draft <-> value ---------------------------------------------------------------------------------------------

/** Text for any value as the screen stores it while someone edits: booleans as "true"/"false", null as empty. */
export function valueToDraft(value: unknown): string {
  if (value === null || value === undefined) return "";
  if (typeof value === "boolean") return value ? "true" : "false";
  return String(value);
}

export const draftOf = (s: Pick<SettingLike, "value">): string => valueToDraft(s.value);

export type DraftError = "required" | "notWhole" | "notNumber" | "tooManyDecimals" | "belowMin" | "aboveMax" | "tooLong" | "notAllowed";

export type Parsed = { ok: true; value: boolean | number | string } | { ok: false; error: DraftError };

const WHOLE = /^[+-]?\d+$/;
const DECIMAL = /^[+-]?\d+(\.\d+)?$/;

/** Converts what is typed into the typed JSON value the API wants, or says what is wrong (the same rules as the backend). */
export function parseDraft(s: Pick<SettingLike, "type" | "min" | "max" | "maxLength" | "allowedValues">, draft: string): Parsed {
  const type = asType(s.type);
  if (type === "bool") return { ok: true, value: draft === "true" };
  if (type === "choice") {
    const wanted = draft.trim().toLowerCase();
    const hit = (s.allowedValues ?? []).find((v) => v.toLowerCase() === wanted);
    return hit === undefined ? { ok: false, error: wanted === "" ? "required" : "notAllowed" } : { ok: true, value: hit };
  }
  if (type === "text") {
    const limit = maxLengthOf(s);
    if (limit !== null && draft.length > limit) return { ok: false, error: "tooLong" };
    return { ok: true, value: draft };
  }
  const text = draft.trim();
  if (text === "") return { ok: false, error: "required" };
  if (type === "whole") {
    if (!WHOLE.test(text)) return { ok: false, error: DECIMAL.test(text) ? "notWhole" : "notNumber" };
  } else {
    if (!DECIMAL.test(text)) return { ok: false, error: "notNumber" };
    const decimals = text.split(".")[1]?.length ?? 0;
    if (decimals > 4) return { ok: false, error: "tooManyDecimals" };
  }
  const n = Number(text);
  if (!Number.isFinite(n)) return { ok: false, error: "notNumber" };
  const lo = minOf(s);
  const hi = maxOf(s);
  if (lo !== null && n < lo) return { ok: false, error: "belowMin" };
  if (hi !== null && n > hi) return { ok: false, error: "aboveMax" };
  return { ok: true, value: n };
}

/** True when the draft would change what is stored now. An invalid draft counts as changed (the Save button then shows the problem). */
export function isDirty(s: Pick<SettingLike, "type" | "value" | "min" | "max" | "maxLength" | "allowedValues">, draft: string | undefined): boolean {
  if (draft === undefined) return false;
  const parsed = parseDraft(s, draft);
  if (!parsed.ok) return draft !== draftOf(s);
  const type = asType(s.type);
  if (type === "whole" || type === "number") return parsed.value !== Number(s.value);
  if (type === "bool") return parsed.value !== (s.value === true);
  return parsed.value !== (s.value === null || s.value === undefined ? "" : String(s.value));
}

// ---- who may do what ----------------------------------------------------------------------------------------------

export type EditBlock = "readOnlyConfig" | "readOnlyFixed" | "noPermission" | null;

/** Why a control is disabled (shown as a plain note), or null when this person may change it. */
export function editBlock(s: Pick<SettingLike, "readOnly" | "source" | "canEdit">): EditBlock {
  if (s.readOnly) return s.source === "appsettings" ? "readOnlyConfig" : "readOnlyFixed";
  if (!s.canEdit) return "noPermission";
  return null;
}

/** A stored row exists, so "Reset to default" does something. */
export const canReset = (s: Pick<SettingLike, "isSet" | "readOnly" | "canEdit">): boolean => s.isSet && !s.readOnly && s.canEdit;

export type SourceKind = "changed" | "default" | "config";
export function sourceKind(source: string): SourceKind {
  return source === "db" ? "changed" : source === "appsettings" ? "config" : "default";
}

// ---- dangerous switches --------------------------------------------------------------------------------------------

export type DangerKind = "loyaltyOn" | "loyaltyOff" | "paymentOn" | "paymentOff" | "autoConfirm" | "otpMode";

/**
 * Settings whose change reaches customers' money or sign-in at once get a confirm that says what happens: the rewards switch,
 * each payment method switch, automatic confirmation of paid orders and the OTP mode of checkout. `next` is the typed new value.
 */
export function dangerKind(key: string, next: boolean | number | string): DangerKind | null {
  if (key === "Loyalty.Enabled") return next === true ? "loyaltyOn" : "loyaltyOff";
  if (key === "Payments.AutoConfirmPaidOrders") return "autoConfirm";
  if (key === "Checkout.OtpMode") return "otpMode";
  if (/^Payments\.[A-Za-z0-9]+\.Enabled$/.test(key)) return next === true ? "paymentOn" : "paymentOff";
  return null;
}

/** The method code of a payment switch (`Payments.BKASH.Enabled` gives `BKASH`), for the confirm text. */
export function paymentMethodOf(key: string): string {
  return /^Payments\.([A-Za-z0-9]+)\.Enabled$/.exec(key)?.[1] ?? key;
}

// ---- grouping and search -------------------------------------------------------------------------------------------

export type Locale = "en" | "bn";

/** The visible name: the description in the current language (falling back to the other one, then to the key). */
export function titleOf(s: Pick<SettingLike, "key" | "descriptionEn" | "descriptionBn">, locale: Locale): string {
  const own = locale === "bn" ? s.descriptionBn : s.descriptionEn;
  const other = locale === "bn" ? s.descriptionEn : s.descriptionBn;
  return own?.trim() || other?.trim() || s.key;
}

/** A setting matches when every word typed appears in its key or in either description (case-insensitive). */
export function matchesQuery(s: Pick<SettingLike, "key" | "descriptionEn" | "descriptionBn">, query: string): boolean {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (words.length === 0) return true;
  const hay = `${s.key} ${s.descriptionEn} ${s.descriptionBn}`.toLowerCase();
  return words.every((w) => hay.includes(w));
}

/** Groups that still have settings after the search; empty groups drop out. Order of groups and settings is the API's. */
export function filterGroups<S extends SettingLike>(groups: readonly GroupLike<S>[], query: string): GroupLike<S>[] {
  return groups.map((g) => ({ group: g.group, settings: g.settings.filter((s) => matchesQuery(s, query)) })).filter((g) => g.settings.length > 0);
}

/** Total settings in the groups. */
export const countSettings = (groups: readonly GroupLike[]): number => groups.reduce((n, g) => n + g.settings.length, 0);

/** The active group tab: the wanted one if it still exists, otherwise the first. */
export function activeGroup(groups: readonly GroupLike[], wanted: string | null): string | null {
  if (groups.length === 0) return null;
  return groups.some((g) => g.group === wanted) ? wanted : groups[0]!.group;
}

// ---- history -------------------------------------------------------------------------------------------------------

export type HistoryLike = { action: string; oldValue: string | null; newValue: string | null };

/** What the history row says happened: first value, changed, or reset to the default. */
export function historyKind(h: HistoryLike): "reset" | "first" | "changed" {
  if (h.action === "reset") return "reset";
  return h.oldValue === null ? "first" : "changed";
}

/** Booleans arrive in history as stored text ("true", "True"); returns the boolean, or null for any other text. */
export function boolOfText(text: string | null): boolean | null {
  if (text === null) return null;
  const t = text.trim().toLowerCase();
  return t === "true" ? true : t === "false" ? false : null;
}
