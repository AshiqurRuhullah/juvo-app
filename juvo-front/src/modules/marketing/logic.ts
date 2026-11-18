import type { Schemas } from "@/lib/api/types";

/** Pure rules of the marketing screens (no React, no network), so they can be tested on their own (docs/marketing-api.md). */

export type CampaignStatus = Schemas["CampaignStatus"];
export type AutomationKind = Schemas["AutomationKind"];
export type SegmentRule = Schemas["SegmentRule"];
export type SegmentDefinition = Schemas["SegmentDefinition"];
export type MarketingChannel = "Sms" | "Email";

export const CAMPAIGN_STATUSES: CampaignStatus[] = ["Draft", "Scheduled", "Sending", "Sent", "Cancelled"];
export const MARKETING_CHANNELS: MarketingChannel[] = ["Sms", "Email"];
export const AUTOMATION_KINDS: AutomationKind[] = ["AbandonedCart", "BackInStock", "ReviewRequest", "WinBack"];

/** The placeholders the API accepts in campaign texts. */
export const CAMPAIGN_PLACEHOLDERS = ["customerName", "shopName", "link", "unsubscribeLink"] as const;

export const NAME_MAX = 120;
export const SEGMENT_RULES_MAX = 20;

/** Order statuses a "last order status" rule can name. */
export const ORDER_STATUSES = ["Placed", "Confirmed", "Packed", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered", "Cancelled", "DeliveryFailed", "ReturnInProgress", "Returned"] as const;

/* ---- Segment rules ---------------------------------------------------------------------------------------------------------- */

export const RULE_TYPES = ["newCustomers", "repeatBuyers", "highSpenders", "inactive", "location", "consent", "lastOrderStatus", "wishlist", "cart"] as const;
export type RuleType = (typeof RULE_TYPES)[number];
export type RuleField = "days" | "minOrders" | "minSpend" | "periodDays" | "level" | "ids" | "channel" | "granted" | "status" | "productIds" | "idleHours";

/** The fields each rule type uses. The API refuses a field its type does not use, so the form shows only these (decision 4 of the backend). */
export const RULE_FIELDS: Record<RuleType, readonly RuleField[]> = {
  newCustomers: ["days"],
  repeatBuyers: ["minOrders", "periodDays"],
  highSpenders: ["minSpend", "periodDays"],
  inactive: ["days"],
  location: ["level", "ids"],
  consent: ["channel", "granted"],
  lastOrderStatus: ["status"],
  wishlist: ["productIds"],
  cart: ["idleHours"],
};

/** Fields that must be filled (the others are optional). */
export const REQUIRED_FIELDS: Record<RuleType, readonly RuleField[]> = {
  newCustomers: ["days"],
  repeatBuyers: ["minOrders"],
  highSpenders: ["minSpend"],
  inactive: ["days"],
  location: ["level", "ids"],
  consent: ["channel"],
  lastOrderStatus: ["status"],
  wishlist: [],
  cart: [],
};

const INTEGER_FIELDS: readonly RuleField[] = ["days", "minOrders", "periodDays", "idleHours"];

/** One rule as the form holds it: every number is the text of an input, every list an array of ids. */
export type RuleForm = {
  key: string;
  type: RuleType;
  days: string;
  minOrders: string;
  minSpend: string;
  periodDays: string;
  level: "division" | "district";
  ids: string[];
  channel: "sms" | "email";
  granted: boolean;
  status: string;
  productIds: string[];
  idleHours: string;
};

export type SegmentForm = { name: string; description: string; combinator: "and" | "or"; rules: RuleForm[] };

let keyCounter = 0;
export const newRuleKey = () => `r${++keyCounter}`;

export function emptyRule(type: RuleType = "newCustomers"): RuleForm {
  return { key: newRuleKey(), type, days: "", minOrders: "", minSpend: "", periodDays: "", level: "district", ids: [], channel: "sms", granted: true, status: "Placed", productIds: [], idleHours: "" };
}

export const emptySegmentForm = (): SegmentForm => ({ name: "", description: "", combinator: "and", rules: [emptyRule()] });

export const isRuleType = (value: string | undefined): value is RuleType => !!value && (RULE_TYPES as readonly string[]).includes(value);

/** Changing the type keeps what the new type also uses (for example days) and drops the rest. */
export function changeRuleType(rule: RuleForm, type: RuleType): RuleForm {
  const fresh = emptyRule(type);
  const next: RuleForm = { ...fresh, key: rule.key };
  for (const field of RULE_FIELDS[type]) if (RULE_FIELDS[rule.type].includes(field)) (next as Record<string, unknown>)[field] = (rule as Record<string, unknown>)[field];
  return next;
}

const numberText = (value: number | string | null | undefined) => (value === null || value === undefined ? "" : String(value));

/** A rule read from the API, for editing. Unknown types fall back to the first type (the API only sends known ones). */
export function ruleFromApi(rule: SegmentRule): RuleForm {
  const type = isRuleType(rule.type) ? rule.type : "newCustomers";
  const form = emptyRule(type);
  return {
    ...form,
    days: numberText(rule.days),
    minOrders: numberText(rule.minOrders),
    minSpend: numberText(rule.minSpend),
    periodDays: numberText(rule.periodDays),
    level: rule.level === "division" ? "division" : "district",
    ids: rule.ids ?? [],
    channel: rule.channel === "email" ? "email" : "sms",
    granted: rule.granted ?? true,
    status: rule.status ?? "Placed",
    productIds: rule.productIds ?? [],
    idleHours: numberText(rule.idleHours),
  };
}

export function segmentFormFromApi(segment: { name: string; description?: string | null; definition: SegmentDefinition }): SegmentForm {
  const rules = (segment.definition.rules ?? []).map(ruleFromApi);
  return { name: segment.name, description: segment.description ?? "", combinator: segment.definition.combinator === "or" ? "or" : "and", rules: rules.length > 0 ? rules : [emptyRule()] };
}

function parse(text: string): number | undefined {
  const trimmed = text.trim().replace(/,/g, "");
  if (trimmed === "") return undefined;
  const n = Number(trimmed);
  return Number.isFinite(n) ? n : Number.NaN;
}

/** Turns one rule of the form into the request body: only the type and the fields the type uses, blanks left out. */
export function ruleToRequest(rule: RuleForm): SegmentRule {
  const out: Record<string, unknown> = { type: rule.type };
  for (const field of RULE_FIELDS[rule.type]) {
    switch (field) {
      case "days":
      case "minOrders":
      case "minSpend":
      case "periodDays":
      case "idleHours": {
        const n = parse(rule[field]);
        if (n !== undefined) out[field] = n;
        break;
      }
      case "level":
        out.level = rule.level;
        break;
      case "ids":
        out.ids = rule.ids;
        break;
      case "channel":
        out.channel = rule.channel;
        break;
      case "granted":
        out.granted = rule.granted;
        break;
      case "status":
        out.status = rule.status;
        break;
      case "productIds":
        if (rule.productIds.length > 0) out.productIds = rule.productIds;
        break;
    }
  }
  return out as SegmentRule;
}

export function segmentToRequest(form: SegmentForm): { name: string; description: string | null; definition: SegmentDefinition } {
  return { name: form.name.trim(), description: form.description.trim() || null, definition: definitionOf(form) };
}

export const definitionOf = (form: Pick<SegmentForm, "combinator" | "rules">): SegmentDefinition => ({ combinator: form.combinator, rules: form.rules.map(ruleToRequest) });

export type RuleProblem = "required" | "number" | "integer" | "positive" | "pick";
export type FormErrors = { name?: "required" | "tooLong"; rules?: string; fields: Record<string, RuleProblem> };

/** The key of a field error: `${ruleIndex}.${field}`. */
export const fieldKey = (index: number, field: RuleField) => `${index}.${field}`;

/** Client check before the request (the API checks again and names the rule and field). Only what a person can fix in the form. */
export function validateSegmentForm(form: SegmentForm): FormErrors {
  const errors: FormErrors = { fields: {} };
  const name = form.name.trim();
  if (!name) errors.name = "required";
  else if (name.length > NAME_MAX) errors.name = "tooLong";
  if (form.rules.length === 0) errors.rules = "none";
  if (form.rules.length > SEGMENT_RULES_MAX) errors.rules = "tooMany";
  form.rules.forEach((rule, index) => {
    for (const field of REQUIRED_FIELDS[rule.type]) {
      const problem = fieldProblem(rule, field, true);
      if (problem) errors.fields[fieldKey(index, field)] = problem;
    }
    for (const field of RULE_FIELDS[rule.type]) {
      if (REQUIRED_FIELDS[rule.type].includes(field)) continue;
      const problem = fieldProblem(rule, field, false);
      if (problem) errors.fields[fieldKey(index, field)] = problem;
    }
  });
  return errors;
}

function fieldProblem(rule: RuleForm, field: RuleField, required: boolean): RuleProblem | null {
  if (field === "ids") return rule.ids.length === 0 ? "pick" : null;
  if (field === "level" || field === "channel" || field === "granted" || field === "status" || field === "productIds") return null;
  const n = parse(rule[field]);
  if (n === undefined) return required ? "required" : null;
  if (Number.isNaN(n)) return "number";
  if (INTEGER_FIELDS.includes(field) && !Number.isInteger(n)) return "integer";
  if (n <= 0) return "positive";
  return null;
}

export const hasFormErrors = (errors: FormErrors) => !!errors.name || !!errors.rules || Object.keys(errors.fields).length > 0;

/** The API names a rule field as `rules[1].minSpend` (possibly with a leading `definition.` and a capital letter). */
export function parseRuleErrorKey(key: string): { index: number; field: string } | null {
  const match = /^(?:definition\.)?rules\[(\d+)\]\.(\w+)$/i.exec(key);
  if (!match) return null;
  const field = match[2]!;
  return { index: Number(match[1]), field: field[0]!.toLowerCase() + field.slice(1) };
}

/** Splits the API's validation errors into the ones that belong to a rule field and the rest (name, definition ...). */
export function splitSegmentErrors(errors: Record<string, string> | undefined): { fields: Record<string, string>; other: string[] } {
  const fields: Record<string, string> = {};
  const other: string[] = [];
  for (const [key, message] of Object.entries(errors ?? {})) {
    const parsed = parseRuleErrorKey(key);
    if (parsed) fields[`${parsed.index}.${parsed.field}`] = message;
    else other.push(message);
  }
  return { fields, other };
}

/** Short description of a rule for the segment list ("Spent at least 10000 in 90 days"), as message keys plus values. */
export function ruleSummary(rule: SegmentRule): { key: string; values: Record<string, string | number> } {
  const type = isRuleType(rule.type) ? rule.type : "newCustomers";
  const n = (v: number | string | null | undefined) => Number(v ?? 0);
  switch (type) {
    case "newCustomers":
      return { key: "newCustomers", values: { days: n(rule.days) } };
    case "repeatBuyers":
      return rule.periodDays ? { key: "repeatBuyersIn", values: { orders: n(rule.minOrders), days: n(rule.periodDays) } } : { key: "repeatBuyers", values: { orders: n(rule.minOrders) } };
    case "highSpenders":
      return rule.periodDays ? { key: "highSpendersIn", values: { amount: n(rule.minSpend), days: n(rule.periodDays) } } : { key: "highSpenders", values: { amount: n(rule.minSpend) } };
    case "inactive":
      return { key: "inactive", values: { days: n(rule.days) } };
    case "location":
      return { key: rule.level === "division" ? "locationDivision" : "locationDistrict", values: { count: rule.ids?.length ?? 0 } };
    case "consent":
      return { key: rule.granted === false ? `${rule.channel === "email" ? "noEmail" : "noSms"}` : `${rule.channel === "email" ? "yesEmail" : "yesSms"}`, values: {} };
    case "lastOrderStatus":
      return { key: "lastOrderStatus", values: { status: rule.status ?? "" } };
    case "wishlist":
      return (rule.productIds?.length ?? 0) > 0 ? { key: "wishlistOf", values: { count: rule.productIds!.length } } : { key: "wishlist", values: {} };
    case "cart":
      return rule.idleHours ? { key: "cartIdle", values: { hours: n(rule.idleHours) } } : { key: "cart", values: {} };
  }
}

/* ---- SMS length ------------------------------------------------------------------------------------------------------------- */

// GSM 03.38 default alphabet and its extension table (an extension character takes two positions).
const GSM_BASIC = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
const GSM_EXTENDED = "^{}\\[~]|€\f";

export type SmsLength = { characters: number; encoding: "gsm" | "unicode"; parts: number; perPart: number; limit: number };

/**
 * How many characters and SMS parts a text takes. Text with only GSM letters is 160 characters in one SMS and 153 per part after
 * that; any other letter (Bengali, for example) switches the whole message to Unicode: 70, then 67 per part. This is an estimate for
 * the template: placeholders such as {link} grow when the message is built, and the opt-out text is added; the preview shows the
 * real text.
 */
export function smsLength(text: string): SmsLength {
  let gsm = 0;
  let isGsm = true;
  for (const ch of text) {
    if (GSM_BASIC.includes(ch)) gsm += 1;
    else if (GSM_EXTENDED.includes(ch)) gsm += 2;
    else {
      isGsm = false;
      break;
    }
  }
  if (isGsm) {
    const parts = gsm <= 160 ? (gsm === 0 ? 0 : 1) : Math.ceil(gsm / 153);
    return { characters: gsm, encoding: "gsm", parts, perPart: parts > 1 ? 153 : 160, limit: 160 };
  }
  const units = text.length; // UTF-16 code units: what Unicode SMS counts
  const parts = units <= 70 ? (units === 0 ? 0 : 1) : Math.ceil(units / 67);
  return { characters: units, encoding: "unicode", parts, perPart: parts > 1 ? 67 : 70, limit: 70 };
}

/** Placeholders written as {name} in a text. */
export function placeholdersIn(text: string): string[] {
  return [...new Set([...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]!))];
}

export function unknownPlaceholders(text: string, allowed: readonly string[] = CAMPAIGN_PLACEHOLDERS): string[] {
  return placeholdersIn(text).filter((p) => !allowed.includes(p));
}

/* ---- Schedule (Dhaka time) -------------------------------------------------------------------------------------------------- */

/** Bangladesh has one offset all year (UTC+06:00, no summer time), so a fixed offset is exact. */
export const DHAKA_OFFSET_HOURS = 6;
const HOUR = 3_600_000;
const LOCAL_FORMAT = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;

/** `2026-12-01T10:00` (what a datetime-local input holds, read as Dhaka time) to the instant it means, or null when it is not a valid time. */
export function dhakaInputToInstant(local: string): Date | null {
  const m = LOCAL_FORMAT.exec(local.trim());
  if (!m) return null;
  const [y, mo, d, h, mi] = m.slice(1).map(Number) as [number, number, number, number, number];
  const utc = Date.UTC(y, mo - 1, d, h, mi);
  const check = new Date(utc);
  if (check.getUTCFullYear() !== y || check.getUTCMonth() !== mo - 1 || check.getUTCDate() !== d || h > 23 || mi > 59) return null;
  return new Date(utc - DHAKA_OFFSET_HOURS * HOUR);
}

/** The value sent to the API: the Dhaka time with its offset, `2026-12-01T10:00:00+06:00`. */
export function dhakaInputToOffset(local: string): string | null {
  return dhakaInputToInstant(local) ? `${local.trim()}:00+06:00` : null;
}

const two = (n: number) => String(n).padStart(2, "0");

/** An instant as `YYYY-MM-DDTHH:mm` in Dhaka time (for the datetime-local input). */
export function instantToDhakaInput(value: string | Date): string {
  const shifted = new Date(new Date(value).getTime() + DHAKA_OFFSET_HOURS * HOUR);
  return `${shifted.getUTCFullYear()}-${two(shifted.getUTCMonth() + 1)}-${two(shifted.getUTCDate())}T${two(shifted.getUTCHours())}:${two(shifted.getUTCMinutes())}`;
}

export type ScheduleProblem = "missing" | "invalid" | "past" | "tooFar";

/** The API wants a future time within a year. Checked here first so the person sees the reason before sending. */
export function scheduleProblem(local: string, now: Date = new Date()): ScheduleProblem | null {
  if (!local.trim()) return "missing";
  const at = dhakaInputToInstant(local);
  if (!at) return "invalid";
  if (at.getTime() <= now.getTime()) return "past";
  if (at.getTime() > now.getTime() + 365 * 24 * HOUR) return "tooFar";
  return null;
}

/* ---- Campaign status -------------------------------------------------------------------------------------------------------- */

export type CampaignAction = "edit" | "schedule" | "sendNow" | "test" | "cancel" | "delete";

/**
 * What can be done with a campaign in each status (docs/marketing-api.md section 4). Draft and scheduled campaigns can be changed,
 * scheduled again, sent and cancelled; a sending one can only be cancelled; a sent one is kept. A test message and the preview work
 * in every status. Delete works for a draft or scheduled campaign, and for one cancelled before it started.
 */
export function allowedActions(status: CampaignStatus, started: boolean): CampaignAction[] {
  switch (status) {
    case "Draft":
    case "Scheduled":
      return ["edit", "schedule", "sendNow", "test", "cancel", "delete"];
    case "Sending":
      return ["test", "cancel"];
    case "Sent":
      return ["test"];
    case "Cancelled":
      return started ? ["test"] : ["test", "delete"];
  }
}

export type Tone = "info" | "warning" | "success" | "danger" | "outline" | "neutral";

export function statusTone(status: CampaignStatus): Tone {
  switch (status) {
    case "Draft":
      return "neutral";
    case "Scheduled":
      return "info";
    case "Sending":
      return "warning";
    case "Sent":
      return "success";
    case "Cancelled":
      return "outline";
  }
}

/** Skip reasons the API reports for recipients (campaigns) and automations. */
export const SKIP_REASONS = ["NoConsent", "NoContact", "FrequencyCap", "ChannelOff", "Cancelled", "Inactive", "OptedOut", "AlreadySent"] as const;

/** Share of a part in a whole as a whole percent (0 when the whole is 0). */
export const percent = (part: number, whole: number) => (whole > 0 ? Math.round((part / whole) * 100) : 0);

/* ---- Campaign form ---------------------------------------------------------------------------------------------------------- */

export type CampaignForm = { name: string; segmentId: string; channel: MarketingChannel; subjectEn: string; subjectBn: string; bodyEn: string; bodyBn: string };

export const emptyCampaignForm = (): CampaignForm => ({ name: "", segmentId: "", channel: "Sms", subjectEn: "", subjectBn: "", bodyEn: "", bodyBn: "" });

export function campaignToRequest(form: CampaignForm) {
  const email = form.channel === "Email";
  return {
    name: form.name.trim(),
    segmentId: form.segmentId || null,
    channel: form.channel,
    subjectEn: email ? form.subjectEn.trim() || null : null,
    subjectBn: email ? form.subjectBn.trim() || null : null,
    bodyEn: form.bodyEn.trim(),
    bodyBn: form.bodyBn.trim() || null,
  };
}

export type CampaignErrors = Partial<Record<"name" | "segmentId" | "subjectEn" | "bodyEn", "required" | "tooLong" | "placeholder">>;

export function validateCampaignForm(form: CampaignForm): CampaignErrors {
  const errors: CampaignErrors = {};
  if (!form.name.trim()) errors.name = "required";
  else if (form.name.trim().length > NAME_MAX) errors.name = "tooLong";
  if (!form.segmentId) errors.segmentId = "required";
  if (!form.bodyEn.trim()) errors.bodyEn = "required";
  else if (unknownPlaceholders(form.bodyEn).length > 0 || unknownPlaceholders(form.bodyBn).length > 0) errors.bodyEn = "placeholder";
  if (form.channel === "Email") {
    if (!form.subjectEn.trim()) errors.subjectEn = "required";
    else if (unknownPlaceholders(form.subjectEn).length > 0 || unknownPlaceholders(form.subjectBn).length > 0) errors.subjectEn = "placeholder";
  }
  return errors;
}

export const hasCampaignErrors = (errors: CampaignErrors) => Object.keys(errors).length > 0;

/* ---- Automations and settings ----------------------------------------------------------------------------------------------- */

/** What the delay field of each automation means (docs/marketing-api.md section 5). */
export const DELAY_MEANING: Record<AutomationKind, "idle" | "stockWait" | "afterDelivery" | "inactivity"> = {
  AbandonedCart: "idle",
  BackInStock: "stockWait",
  ReviewRequest: "afterDelivery",
  WinBack: "inactivity",
};

/** Does the window field apply? The API does not use it for win-back and back-in-stock. */
export const usesWindow = (kind: AutomationKind) => kind === "AbandonedCart" || kind === "ReviewRequest";

/** Does the automation need consent? Back in stock is a requested message (opt-out honoured), the others are marketing. */
export const needsConsent = (kind: AutomationKind) => kind !== "BackInStock";

export type AutomationForm = {
  isEnabled: boolean;
  channel: MarketingChannel;
  delayMinutes: string;
  windowDays: string;
  capDays: string;
  subjectEn: string;
  subjectBn: string;
  bodyEn: string;
  bodyBn: string;
};

type AutomationLike = { isEnabled: boolean; channel: string; delayMinutes: number | string; windowDays: number | string; capDays: number | string; subjectEn: string | null; subjectBn: string | null; bodyEn: string; bodyBn: string };

export function automationFormFrom(a: AutomationLike): AutomationForm {
  return {
    isEnabled: a.isEnabled,
    channel: a.channel === "Email" ? "Email" : "Sms",
    delayMinutes: String(a.delayMinutes),
    windowDays: String(a.windowDays),
    capDays: String(a.capDays),
    subjectEn: a.subjectEn ?? "",
    subjectBn: a.subjectBn ?? "",
    bodyEn: a.bodyEn,
    bodyBn: a.bodyBn,
  };
}

export type AutomationErrors = Partial<Record<"delayMinutes" | "windowDays" | "capDays" | "bodyEn" | "subjectEn", "number" | "integer" | "negative" | "required" | "placeholder">>;

export function validateAutomationForm(kind: AutomationKind, form: AutomationForm, allowed: readonly string[]): AutomationErrors {
  const errors: AutomationErrors = {};
  const check = (field: "delayMinutes" | "windowDays" | "capDays") => {
    const n = parse(form[field]);
    if (n === undefined || Number.isNaN(n)) errors[field] = "number";
    else if (!Number.isInteger(n)) errors[field] = "integer";
    else if (n < 0) errors[field] = "negative";
  };
  check("delayMinutes");
  if (usesWindow(kind)) check("windowDays");
  check("capDays");
  if (!form.bodyEn.trim()) errors.bodyEn = "required";
  else if (unknownPlaceholders(form.bodyEn, allowed).length > 0 || unknownPlaceholders(form.bodyBn, allowed).length > 0) errors.bodyEn = "placeholder";
  if (form.channel === "Email" && !form.subjectEn.trim()) errors.subjectEn = "required";
  return errors;
}

/** The request for the fields that changed (the API changes only what is sent). */
export function automationPatch(kind: AutomationKind, form: AutomationForm) {
  const email = form.channel === "Email";
  return {
    isEnabled: form.isEnabled,
    channel: form.channel,
    delayMinutes: Number(form.delayMinutes),
    windowDays: usesWindow(kind) ? Number(form.windowDays) : undefined,
    capDays: Number(form.capDays),
    subjectEn: email ? form.subjectEn.trim() : undefined,
    subjectBn: email ? form.subjectBn.trim() : undefined,
    bodyEn: form.bodyEn.trim(),
    bodyBn: form.bodyBn.trim(),
  };
}

/** Delay in minutes as a readable amount: "3 hours", "7 days" (the unit and the count, for translation). */
export function delayParts(minutes: number): { unit: "minutes" | "hours" | "days"; count: number } {
  if (minutes > 0 && minutes % 1440 === 0) return { unit: "days", count: minutes / 1440 };
  if (minutes > 0 && minutes % 60 === 0) return { unit: "hours", count: minutes / 60 };
  return { unit: "minutes", count: minutes };
}

export type SettingsForm = {
  quietHoursEnabled: boolean;
  quietHoursStart: string;
  quietHoursEnd: string;
  frequencyCapMessages: string;
  frequencyCapDays: string;
  throttlePerMinute: string;
  attributionWindowDays: string;
};

type SettingsLike = { quietHoursEnabled: boolean; quietHoursStart: string; quietHoursEnd: string; frequencyCapMessages: number | string; frequencyCapDays: number | string; throttlePerMinute: number | string; attributionWindowDays: number | string };

export const settingsFormFrom = (s: SettingsLike): SettingsForm => ({
  quietHoursEnabled: s.quietHoursEnabled,
  quietHoursStart: s.quietHoursStart,
  quietHoursEnd: s.quietHoursEnd,
  frequencyCapMessages: String(s.frequencyCapMessages),
  frequencyCapDays: String(s.frequencyCapDays),
  throttlePerMinute: String(s.throttlePerMinute),
  attributionWindowDays: String(s.attributionWindowDays),
});

const HHMM = /^([01]\d|2[0-3]):[0-5]\d$/;
export const SETTINGS_RANGES = {
  frequencyCapMessages: [0, 100],
  frequencyCapDays: [1, 365],
  throttlePerMinute: [1, 100000],
  attributionWindowDays: [1, 90],
} as const;

export type SettingsErrors = Partial<Record<keyof SettingsForm, "range" | "time" | "integer">>;

export function validateSettingsForm(form: SettingsForm): SettingsErrors {
  const errors: SettingsErrors = {};
  if (!HHMM.test(form.quietHoursStart)) errors.quietHoursStart = "time";
  if (!HHMM.test(form.quietHoursEnd)) errors.quietHoursEnd = "time";
  for (const field of Object.keys(SETTINGS_RANGES) as (keyof typeof SETTINGS_RANGES)[]) {
    const n = parse(form[field]);
    const [min, max] = SETTINGS_RANGES[field];
    if (n === undefined || Number.isNaN(n)) errors[field] = "range";
    else if (!Number.isInteger(n)) errors[field] = "integer";
    else if (n < min || n > max) errors[field] = "range";
  }
  return errors;
}

export function settingsToRequest(form: SettingsForm) {
  return {
    quietHoursEnabled: form.quietHoursEnabled,
    quietHoursStart: form.quietHoursStart,
    quietHoursEnd: form.quietHoursEnd,
    frequencyCapMessages: Number(form.frequencyCapMessages),
    frequencyCapDays: Number(form.frequencyCapDays),
    throttlePerMinute: Number(form.throttlePerMinute),
    attributionWindowDays: Number(form.attributionWindowDays),
    // Not edited on this screen: null means "leave as it is" (the API changes only the fields it is given).
    shopName: null,
    linkUrl: null,
    maxNotifyMePerMobile: null,
    smsOptOutText: null,
  };
}

/* ---- Storefront: notify me and unsubscribe ---------------------------------------------------------------------------------- */

/** The text key (under `marketingStore.notify.errors`) for a failed "notify me" request, from the API's code and status. */
export function notifyErrorKey(problem: { status?: number; code?: string } | null | undefined): "inStock" | "limit" | "contact" | "notFound" | "rateLimited" | "network" | "generic" {
  if (!problem) return "generic";
  if (problem.status === 0 || problem.code === "NETWORK") return "network";
  switch (problem.code) {
    case "MARKETING.VARIANT_IN_STOCK":
      return "inStock";
    case "MARKETING.SUBSCRIPTION_LIMIT":
      return "limit";
    case "MARKETING.CONTACT_REQUIRED":
      return "contact";
    case "MARKETING.VARIANT_NOT_FOUND":
      return "notFound";
  }
  // The per-address rate limit answers 429 without a marketing code; the number limit has its own code (above).
  if (problem.status === 429) return "rateLimited";
  return "generic";
}

/** Can the "notify me" form be offered for this variant? A sold-out variant that can be pre-ordered is bought instead. */
export function canNotify(variant: { inStock: boolean; isPreorder?: boolean } | undefined | null): boolean {
  return !!variant && !variant.inStock && !variant.isPreorder;
}

/** A Bangladeshi mobile number as people type it: 11 digits starting 01, or +880 / 880 followed by 10 digits. Bengali digits are accepted. */
export function looksLikeMobile(value: string): boolean {
  const digits = toLatinDigits(value).replace(/[\s-]/g, "");
  return /^(?:\+?880|0)?1[3-9]\d{8}$/.test(digits);
}

export function toLatinDigits(value: string): string {
  return value.replace(/[০-৯]/g, (d) => String("০১২৩৪৫৬৭৮৯".indexOf(d)));
}

export type UnsubscribeState = "missing" | "ready" | "working" | "done" | "already" | "invalid" | "failed";

/** What the unsubscribe page shows after a call (or before one, when the link has no token). */
export function unsubscribeOutcome(args: { token: string | null | undefined; result?: { unsubscribed: boolean; alreadyUnsubscribed: boolean } | null; problem?: { status?: number; code?: string } | null; working?: boolean }): UnsubscribeState {
  if (!args.token || !args.token.trim()) return "missing";
  if (args.working) return "working";
  if (args.result) return args.result.alreadyUnsubscribed ? "already" : "done";
  if (args.problem) return args.problem.code === "MARKETING.UNSUBSCRIBE_TOKEN_INVALID" ? "invalid" : "failed";
  return "ready";
}
