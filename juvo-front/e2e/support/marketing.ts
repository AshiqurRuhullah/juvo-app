import { expect } from "@playwright/test";
import { api, sql } from "./env";
import { adminAccess, customerAccess, freshMobile } from "./returns";

/**
 * Data for the marketing specs, made through the real API: customers who agreed to SMS, segments, campaigns and the sending rules.
 * Quiet hours and the frequency cap are switched off for the run (default quiet hours would hold an SMS back at night and make the
 * specs depend on the time of day); the specs that test those settings put them back.
 */
export type Json = Record<string, unknown>;

export const uniqueName = (prefix: string) => `${prefix} ${Math.random().toString(36).slice(2, 8)}`;

const NOT_SENT = { shopName: null, linkUrl: null, maxNotifyMePerMobile: null, smsOptOutText: null };

export async function setSettings(patch: Json): Promise<void> {
  const token = await adminAccess();
  const response = await api("PUT", "/api/v1/marketing/settings", {
    quietHoursEnabled: null,
    quietHoursStart: null,
    quietHoursEnd: null,
    frequencyCapMessages: null,
    frequencyCapDays: null,
    throttlePerMinute: null,
    attributionWindowDays: null,
    ...NOT_SENT,
    ...patch,
  }, token);
  expect(response.status, JSON.stringify(response.json)).toBe(200);
}

/** Quiet hours off and no frequency cap: sending is then independent of the clock and of earlier specs. */
export const openSending = () => setSettings({ quietHoursEnabled: false, frequencyCapMessages: 0 });

/** The number as the API stores it (+880...). */
export const normalized = (mobile: string) => `+88${mobile}`;

/** A customer (sign in through the API) who agreed to marketing SMS, or declined it. */
export async function customer(options: { sms?: boolean | null } = {}): Promise<{ mobile: string; token: string }> {
  const mobile = freshMobile();
  const token = await customerAccess(mobile);
  if (options.sms !== null) {
    const response = await api("PUT", "/api/v1/me/consent", { type: "MarketingSms", granted: options.sms ?? true }, token);
    expect(response.status, JSON.stringify(response.json)).toBe(200);
  }
  return { mobile, token };
}

export type SegmentRef = { id: string; name: string };

/** Customers of this run that agreed to SMS: consent granted AND joined in the last day. */
export async function consentedSegment(name = uniqueName("Consented")): Promise<SegmentRef> {
  const token = await adminAccess();
  const response = await api<{ id: string }>("POST", "/api/v1/marketing/segments", {
    name,
    description: "e2e",
    definition: { combinator: "and", rules: [{ type: "consent", channel: "sms", granted: true }, { type: "newCustomers", days: 1 }] },
  }, token);
  expect(response.status, JSON.stringify(response.json)).toBe(201);
  return { id: response.json.id, name };
}

export type CampaignRef = { id: string; name: string };

export async function campaign(segmentId: string, options: { name?: string; channel?: "Sms" | "Email"; body?: string } = {}): Promise<CampaignRef> {
  const token = await adminAccess();
  const name = options.name ?? uniqueName("Campaign");
  const channel = options.channel ?? "Sms";
  const response = await api<{ id: string }>("POST", "/api/v1/marketing/campaigns", {
    name,
    segmentId,
    channel,
    subjectEn: channel === "Email" ? "E2E subject" : null,
    subjectBn: null,
    bodyEn: options.body ?? "Hi {customerName}, news at {shopName}: {link}",
    bodyBn: "হ্যালো {customerName}, {shopName}-এ নতুন: {link}",
  }, token);
  expect(response.status, JSON.stringify(response.json)).toBe(201);
  return { id: response.json.id, name };
}

/** The newest message body written for this number (the sandbox sender writes the notification log). */
export function lastMessageTo(mobile: string): string {
  return sql(`SELECT TOP 1 Body FROM NotificationLogs WHERE Recipient = N'${normalized(mobile)}' AND Template = N'marketing.campaign' ORDER BY QueuedAt DESC`);
}

/** The unsubscribe token inside a message body (the link ends in ?token=...). */
export function tokenIn(body: string): string {
  const match = body.match(/token=([A-Za-z0-9_\-%.~]+)/);
  if (!match) throw new Error(`No unsubscribe link in: ${body}`);
  return decodeURIComponent(match[1]!);
}

export async function marketingConsent(token: string): Promise<boolean | undefined> {
  const response = await api<{ type: string; granted: boolean }[]>("GET", "/api/v1/me/consent", undefined, token);
  return response.json.find((c) => c.type === "MarketingSms")?.granted;
}

export type OptionedProduct = { slug: string; productId: string; variants: { id: string; colorName: string | null; size: string | null }[] };

/** A published sample product with at least two sizes (so no option is preselected on its page), straight from the public API. */
export async function productWithOptions(): Promise<OptionedProduct> {
  const list = await api<{ items: { slug: string }[] }>("GET", "/api/v1/store/products?pageSize=24");
  for (const item of list.json.items) {
    const detail = await api<{ card: { id: string }; variants: OptionedProduct["variants"] }>("GET", `/api/v1/store/products/${item.slug}`);
    if (detail.status === 200 && new Set(detail.json.variants.map((v) => v.size).filter(Boolean)).size >= 2) return { slug: item.slug, productId: detail.json.card.id, variants: detail.json.variants };
  }
  throw new Error("The sample catalogue has no product with two sizes.");
}
