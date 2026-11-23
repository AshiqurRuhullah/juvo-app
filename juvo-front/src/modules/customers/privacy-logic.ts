import type { ApiProblem } from "@/lib/api/types";

/**
 * Pure rules of the customer privacy screens (consent history, data export, anonymisation). No React and no network, so they are
 * tested on their own (docs/privacy-api.md). The API checks everything again; these rules only decide what the screen offers.
 */

export const REASON_MIN = 5;
export const REASON_MAX = 300;

export type PrivacyRights = { exportData: boolean; anonymise: boolean };

/** The placeholder mobile the API writes on an anonymised customer starts with ANON (real numbers start with a plus sign). */
export const isAnonymisedMobile = (mobile: string | null | undefined): boolean => !!mobile && mobile.toUpperCase().startsWith("ANON");

/** The API accepts the customer id in any letter case, with or without dashes, so the screen does the same. */
export const normaliseConfirm = (value: string): string => value.replaceAll("-", "").replaceAll(/\s+/g, "").toLowerCase();

export const confirmMatches = (typed: string, customerId: string): boolean => {
  const wanted = normaliseConfirm(customerId);
  return wanted.length > 0 && normaliseConfirm(typed) === wanted;
};

export type ReasonProblem = "tooShort" | "tooLong" | null;

/** The reason is counted after trimming, like the server does (5 to 300 characters). */
export function reasonProblem(reason: string): ReasonProblem {
  const length = [...reason.trim()].length;
  if (length < REASON_MIN) return "tooShort";
  if (length > REASON_MAX) return "tooLong";
  return null;
}

/** The anonymise button is enabled only with a valid reason AND the matching customer id, and never while a request runs. */
export function canAnonymise(input: { reason: string; confirm: string; customerId: string; busy?: boolean }): boolean {
  return !input.busy && reasonProblem(input.reason) === null && confirmMatches(input.confirm, input.customerId);
}

/** Which of the cards a staff member sees on the customer page, from the permissions held. */
export function privacySections(rights: { view: boolean } & PrivacyRights): { history: boolean; privacyCard: boolean } {
  return { history: rights.view, privacyCard: rights.exportData || rights.anonymise };
}

export type PrivacyErrorKind = "openWork" | "staffAccount" | "exportLimit" | "reason" | "confirm" | "other";

/** Groups the API's privacy error codes so the dialog can show the right kind of message (the words are in errors.PRIVACY_*). */
export function privacyErrorKind(problem: Pick<ApiProblem, "code" | "status"> | null | undefined): PrivacyErrorKind {
  switch (problem?.code) {
    case "PRIVACY.OPEN_ORDERS":
      return "openWork";
    case "PRIVACY.STAFF_ACCOUNT":
      return "staffAccount";
    case "PRIVACY.EXPORT_LIMIT":
      return "exportLimit";
    case "PRIVACY.REASON_REQUIRED":
      return "reason";
    case "PRIVACY.CONFIRM_MISMATCH":
      return "confirm";
    default:
      return "other";
  }
}

/** A refusal that means "cannot be done now": the dialog stays open and says why, instead of a short toast. */
export const isBlockingKind = (kind: PrivacyErrorKind): boolean => kind === "openWork" || kind === "staffAccount";

export type ConsentChange = "firstGiven" | "firstDeclined" | "given" | "withdrawn";
export type ConsentSourceKind = "Customer" | "Staff" | "UnsubscribeLink" | "Import" | "Other";
export type ConsentChannel = "Sms" | "Email" | "Policy";

export type ConsentEntryInput = {
  id: string;
  type: string;
  channel: string;
  appliesTo: readonly string[];
  oldValue: boolean | null;
  newValue: boolean;
  sourceKind: string;
  source: string;
  ipAddress: string | null;
  at: string;
};

export type ConsentRow = {
  id: string;
  at: string;
  channel: ConsentChannel | "unknown";
  /** True when an SMS consent also covers WhatsApp (the API lists both in appliesTo). */
  coversWhatsApp: boolean;
  change: ConsentChange;
  sourceKind: ConsentSourceKind;
  /** The source text as stored (for example `unsubscribe:<campaign>`), shown small under the friendly source name. */
  source: string;
  /** Exactly as the API sends it (partly hidden without the personal-data permission); null when there is none. */
  ip: string | null;
};

const CHANNELS: readonly string[] = ["Sms", "Email", "Policy"];
const SOURCE_KINDS: readonly string[] = ["Customer", "Staff", "UnsubscribeLink", "Import", "Other"];

export function consentChange(oldValue: boolean | null, newValue: boolean): ConsentChange {
  if (oldValue === null) return newValue ? "firstGiven" : "firstDeclined";
  return newValue ? "given" : "withdrawn";
}

/** One API history entry as the row the table and the phone cards show. Unknown channel or source kinds fall back instead of breaking. */
export function toConsentRow(entry: ConsentEntryInput): ConsentRow {
  return {
    id: entry.id,
    at: entry.at,
    channel: CHANNELS.includes(entry.channel) ? (entry.channel as ConsentChannel) : "unknown",
    coversWhatsApp: entry.appliesTo.includes("WhatsApp"),
    change: consentChange(entry.oldValue, entry.newValue),
    sourceKind: SOURCE_KINDS.includes(entry.sourceKind) ? (entry.sourceKind as ConsentSourceKind) : "Other",
    source: entry.source,
    ip: entry.ipAddress && entry.ipAddress.trim() ? entry.ipAddress : null,
  };
}

/** The file name when the server sends none. */
export const exportFallbackName = (customerId: string): string => `customer-data-${customerId}.json`;
