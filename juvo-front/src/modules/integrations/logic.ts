/**
 * Pure rules of the API keys and webhooks screens (FR-AF05; docs/public-api.md). No React, no network: scope and event lists, form
 * validation that mirrors the API, the Dhaka expiry date, delivery log filters and the help texts' code samples live here so they can
 * be unit tested. The API stays the judge: it re-checks everything, and its own answers are shown when it refuses.
 */

/** The fixed scope list in the order the screens show it (docs/public-api.md section 1). All scopes are read only. */
export const API_SCOPES = ["orders:read", "products:read", "stock:read", "customers:read", "pii:read"] as const;
export type ApiScope = (typeof API_SCOPES)[number];
/** This scope is not an endpoint: it makes personal data readable, so the screen warns and never preselects it. */
export const PII_SCOPE: ApiScope = "pii:read";

export const NAME_MIN = 2;
export const NAME_MAX = 80;
export const RATE_MIN = 1;
export const RATE_MAX = 10_000;
export const URL_MAX = 500;
/** The API allows 20 endpoints; the screen says so before it is refused. */
export const MAX_ENDPOINTS = 20;

/** The scopes to show: the API's list (so a later scope appears at once) in our order; unknown ones follow in the API's order. */
export function orderScopes<T extends { scope: string }>(fromApi: readonly T[]): T[] {
  const rank = (scope: string) => {
    const i = (API_SCOPES as readonly string[]).indexOf(scope);
    return i === -1 ? API_SCOPES.length : i;
  };
  return fromApi.map((s, index) => ({ s, index })).sort((a, b) => rank(a.s.scope) - rank(b.s.scope) || a.index - b.index).map(({ s }) => s);
}

/** Message key of a scope (a colon is not nice in a key path): `orders:read` becomes `orders_read`. */
export const scopeKey = (scope: string): string => scope.replace(":", "_");
export const isKnownScope = (scope: string): scope is ApiScope => (API_SCOPES as readonly string[]).includes(scope);
export const needsPiiWarning = (scopes: readonly string[]): boolean => scopes.includes(PII_SCOPE);

// ---- API key form ------------------------------------------------------------------------------------------------------------------

export type KeyForm = {
  name: string;
  scopes: string[];
  /** Branch id, or "" for every branch. */
  branchId: string;
  /** A day in Asia/Dhaka (yyyy-mm-dd), or "" for no expiry. */
  expiresOn: string;
  /** Digits, or "" for the default limit. */
  rateLimit: string;
};

export type KeyErrorCode = "nameLength" | "noScope" | "unknownScope" | "expiryInvalid" | "expiryPast" | "rateInvalid";
export type KeyErrors = Partial<Record<"name" | "scopes" | "expiresOn" | "rateLimit", KeyErrorCode>>;

export const emptyKeyForm = (): KeyForm => ({ name: "", scopes: [], branchId: "", expiresOn: "", rateLimit: "" });

/** The end of the chosen Dhaka day (UTC+6, no daylight saving) as an ISO instant with offset: the key works through that whole day. */
export function expiryToIso(day: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(day)) return null;
  const iso = `${day}T23:59:59+06:00`;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  // new Date rolls 2026-02-31 over to March; compare the day back in Dhaka to refuse such dates.
  return isoToDhakaDay(iso) === day ? iso : null;
}

/** A moment as a calendar day in Asia/Dhaka (empty for no moment). */
export function isoToDhakaDay(iso: string | null | undefined): string {
  if (!iso) return "";
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  return new Intl.DateTimeFormat("en-CA", { timeZone: "Asia/Dhaka", year: "numeric", month: "2-digit", day: "2-digit" }).format(date);
}

/** Today in Asia/Dhaka, for the date field's minimum. */
export const dhakaToday = (now: number = Date.now()): string => isoToDhakaDay(new Date(now).toISOString());

export function parseRate(text: string): number | null {
  const t = text.trim();
  if (!/^\d{1,6}$/.test(t)) return null;
  const n = Number(t);
  return n >= RATE_MIN && n <= RATE_MAX ? n : null;
}

export function validateKey(form: KeyForm, knownScopes: readonly string[] = API_SCOPES, now: number = Date.now()): KeyErrors {
  const errors: KeyErrors = {};
  const name = form.name.trim();
  if (name.length < NAME_MIN || name.length > NAME_MAX) errors.name = "nameLength";
  if (form.scopes.length === 0) errors.scopes = "noScope";
  else if (form.scopes.some((s) => !knownScopes.includes(s))) errors.scopes = "unknownScope";
  if (form.expiresOn) {
    const iso = expiryToIso(form.expiresOn);
    if (!iso) errors.expiresOn = "expiryInvalid";
    else if (new Date(iso).getTime() <= now) errors.expiresOn = "expiryPast";
  }
  if (form.rateLimit.trim() !== "" && parseRate(form.rateLimit) === null) errors.rateLimit = "rateInvalid";
  return errors;
}

export const hasErrors = (errors: object): boolean => Object.keys(errors).length > 0;

export type KeyRequest = { name: string; scopes: string[]; branchId: string | null; expiresAt: string | null; rateLimitPerMinute: number | null };

export function keyToRequest(form: KeyForm): KeyRequest {
  return {
    name: form.name.trim(),
    scopes: [...API_SCOPES.filter((s) => form.scopes.includes(s)), ...form.scopes.filter((s) => !isKnownScope(s))],
    branchId: form.branchId || null,
    expiresAt: form.expiresOn ? expiryToIso(form.expiresOn) : null,
    rateLimitPerMinute: form.rateLimit.trim() === "" ? null : parseRate(form.rateLimit),
  };
}

type KeyLike = { name: string; scopes: string[]; branchId?: string | null; expiresAt?: string | null; rateLimitPerMinute?: number | string | null };

export function keyToForm(key: KeyLike): KeyForm {
  return {
    name: key.name,
    scopes: [...key.scopes],
    branchId: key.branchId ?? "",
    expiresOn: isoToDhakaDay(key.expiresAt),
    rateLimit: key.rateLimitPerMinute === null || key.rateLimitPerMinute === undefined ? "" : String(key.rateLimitPerMinute),
  };
}

export type KeyState = "Enabled" | "Disabled" | "Expired";

/** What the list shows. Expired wins over enabled (the API refuses an expired key whatever its switch says). */
export function keyState(key: { isEnabled: boolean; isExpired: boolean }): KeyState {
  if (key.isExpired) return "Expired";
  return key.isEnabled ? "Enabled" : "Disabled";
}

export function toggleScope(scopes: readonly string[], scope: string, on: boolean): string[] {
  const without = scopes.filter((s) => s !== scope);
  return on ? [...without, scope] : without;
}

// ---- Webhooks ----------------------------------------------------------------------------------------------------------------------

/** The five events an endpoint can subscribe to (`ping` is only sent by the test button). */
export const WEBHOOK_EVENTS = ["order.placed", "order.status_changed", "product.published", "product.updated", "stock.low"] as const;
export type WebhookEvent = (typeof WEBHOOK_EVENTS)[number];
/** Message key of an event (no dots in key paths): `order.status_changed` becomes `order_status_changed`. */
export const eventKey = (event: string): string => event.replaceAll(".", "_");
export const isKnownEvent = (e: string): e is WebhookEvent => (WEBHOOK_EVENTS as readonly string[]).includes(e);

export type UrlCheck = "ok" | "empty" | "tooLong" | "invalid" | "notHttps" | "credentials";

/**
 * The client-side part of the address check, mirroring the API: an https address, at most 500 characters, no user name or password.
 * Whether the host is a public address (loopback, private and link-local ranges are refused) only the API can decide, because it
 * resolves the name; its refusal text is shown as it comes.
 */
export function checkWebhookUrl(raw: string): UrlCheck {
  const url = raw.trim();
  if (url.length === 0) return "empty";
  if (url.length > URL_MAX) return "tooLong";
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return "invalid";
  }
  if (parsed.protocol !== "https:") return "notHttps";
  if (parsed.username || parsed.password) return "credentials";
  if (!parsed.hostname) return "invalid";
  return "ok";
}

export type WebhookForm = { name: string; url: string; events: string[]; isEnabled: boolean };
export type WebhookErrorCode = "nameLength" | "noEvent" | "unknownEvent" | Exclude<UrlCheck, "ok">;
export type WebhookErrors = Partial<Record<"name" | "url" | "events", WebhookErrorCode>>;

export const emptyWebhookForm = (): WebhookForm => ({ name: "", url: "", events: [], isEnabled: true });

export function validateWebhook(form: WebhookForm): WebhookErrors {
  const errors: WebhookErrors = {};
  const name = form.name.trim();
  if (name.length < NAME_MIN || name.length > NAME_MAX) errors.name = "nameLength";
  const url = checkWebhookUrl(form.url);
  if (url !== "ok") errors.url = url;
  if (form.events.length === 0) errors.events = "noEvent";
  else if (form.events.some((e) => !isKnownEvent(e))) errors.events = "unknownEvent";
  return errors;
}

export type WebhookRequest = { name: string; url: string; events: string[]; isEnabled: boolean };

export function webhookToRequest(form: WebhookForm): WebhookRequest {
  return { name: form.name.trim(), url: form.url.trim(), events: WEBHOOK_EVENTS.filter((e) => form.events.includes(e)), isEnabled: form.isEnabled };
}

export function webhookToForm(endpoint: { name: string; url: string; events: string[]; isEnabled: boolean }): WebhookForm {
  return { name: endpoint.name, url: endpoint.url, events: [...endpoint.events], isEnabled: endpoint.isEnabled };
}

// ---- Delivery log ------------------------------------------------------------------------------------------------------------------

export const DELIVERY_STATUSES = ["Pending", "Succeeded", "Failed"] as const;
export type DeliveryStatus = (typeof DELIVERY_STATUSES)[number];
/** The log also shows `ping` rows written by the test button. */
export const LOG_EVENTS = [...WEBHOOK_EVENTS, "ping"] as const;

export type DeliveryFilters = { endpointId?: string; status?: string; eventType?: string };

/** The query of the delivery log; empty filters are left out (the API treats a missing value as "any"). */
export function deliveryQuery(args: { page: number; pageSize: number; search?: string }, filters: DeliveryFilters): Record<string, string | number> {
  const q: Record<string, string | number> = { Page: args.page, PageSize: args.pageSize };
  if (args.search) q.Search = args.search;
  if (filters.endpointId) q.endpointId = filters.endpointId;
  if (filters.status && (DELIVERY_STATUSES as readonly string[]).includes(filters.status)) q.status = filters.status;
  if (filters.eventType) q.eventType = filters.eventType;
  return q;
}

export const anyDeliveryFilter = (f: DeliveryFilters, search: string): boolean => !!(f.endpointId || f.status || f.eventType || search);

/** Failed and succeeded deliveries can be sent again; a pending one answers 409 INTEGRATIONS.NOT_REDELIVERABLE. */
export const canRedeliver = (status: string): boolean => status === "Failed" || status === "Succeeded";

export type DeliveryTone = "info" | "success" | "danger";
export const DELIVERY_TONE: Record<DeliveryStatus, DeliveryTone> = { Pending: "info", Succeeded: "success", Failed: "danger" };

/** Summary of sending several deliveries again one by one: how many were accepted and how many the API refused. */
export function summariseRedelivery(outcomes: readonly boolean[]): { sent: number; refused: number } {
  const sent = outcomes.filter(Boolean).length;
  return { sent, refused: outcomes.length - sent };
}

// ---- Help texts (code samples with placeholders only) -------------------------------------------------------------------------------

/** The public API's path on the API host (the staff site does not proxy it). */
export const PUBLIC_API_PATH = "/api/public/v1";
export const PUBLIC_OPENAPI_PATH = "/openapi/public-v1.json";
export const API_KEY_PLACEHOLDER = "YOUR_API_KEY";
export const SECRET_PLACEHOLDER = "YOUR_WEBHOOK_SECRET";
export const HOST_PLACEHOLDER = "YOUR_API_HOST";
export const TOLERANCE_SECONDS = 300;

export function curlExample(): string {
  return `curl -H "X-Api-Key: ${API_KEY_PLACEHOLDER}" \\\n  "https://${HOST_PLACEHOLDER}${PUBLIC_API_PATH}/products?pageSize=10"`;
}

/** Node sample that checks `Juvo-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body">` and refuses a timestamp more than 5 minutes off. */
export function verifySample(): string {
  return [
    `import { createHmac, timingSafeEqual } from "node:crypto";`,
    ``,
    `const secret = process.env.${SECRET_PLACEHOLDER}; // the whsec_... value shown once`,
    ``,
    `export function isFromJuvo(rawBody, signatureHeader) {`,
    `  const parts = Object.fromEntries(signatureHeader.split(",").map((p) => p.split("=")));`,
    `  const t = Number(parts.t);`,
    `  if (!Number.isFinite(t) || Math.abs(Date.now() / 1000 - t) > ${TOLERANCE_SECONDS}) return false; // too old or from the future`,
    `  const expected = createHmac("sha256", secret).update(\`\${parts.t}.\${rawBody}\`).digest("hex");`,
    `  const given = Buffer.from(parts.v1 ?? "", "hex");`,
    `  const wanted = Buffer.from(expected, "hex");`,
    `  return given.length === wanted.length && timingSafeEqual(given, wanted);`,
    `}`,
  ].join("\n");
}
