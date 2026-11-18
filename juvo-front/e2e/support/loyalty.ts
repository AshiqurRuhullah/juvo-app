import { expect } from "@playwright/test";
import { api } from "./env";

/**
 * Data for the rewards specs, prepared through the real API (the browser tests then look at the screens): feature settings, a
 * customer's id, manual adjustments and gift cards. Every call is made as the Super Admin.
 */

export async function setLoyalty(token: string, patch: Record<string, unknown>) {
  const result = await api("PUT", "/api/v1/loyalty/settings", patch, token);
  expect(result.status, JSON.stringify(result.json)).toBe(200);
}

/** The id of the customer with this mobile number (the customer exists once the number has signed in or ordered). */
export async function customerIdOf(token: string, mobile: string): Promise<string> {
  const found = await api<{ items: { id: string }[] }>("GET", `/api/v1/customers?Search=${encodeURIComponent(mobile)}&PageSize=5`, undefined, token);
  expect(found.status).toBe(200);
  expect(found.json.items.length, `no customer for ${mobile}`).toBeGreaterThan(0);
  return found.json.items[0]!.id;
}

export async function adjust(token: string, customerId: string, body: { points?: number; credit?: number }) {
  const result = await api("POST", `/api/v1/loyalty/customers/${customerId}/adjust`, { points: body.points ?? null, credit: body.credit ?? null, reason: "e2e set up" }, token);
  expect(result.status, JSON.stringify(result.json)).toBe(200);
}

export async function balances(token: string, customerId: string): Promise<{ points: number; credit: number }> {
  const result = await api<{ pointsBalance: number | string; creditBalance: number | string }>("GET", `/api/v1/loyalty/customers/${customerId}`, undefined, token);
  expect(result.status).toBe(200);
  return { points: Number(result.json.pointsBalance), credit: Number(result.json.creditBalance) };
}

/** Creates a gift card through the API and returns its id and the code (shown only in this answer). */
export async function createCard(token: string, amount: number): Promise<{ id: string; code: string }> {
  const result = await api<{ card: { id: string }; code: string }>("POST", "/api/v1/loyalty/gift-cards", { amount, expiresAt: null, note: "e2e card", recipientMobile: null, recipientEmail: null, sendToRecipient: false }, token);
  expect(result.status, JSON.stringify(result.json)).toBe(201);
  return { id: result.json.card.id, code: result.json.code };
}

export async function deactivateCard(token: string, id: string) {
  const result = await api("POST", `/api/v1/loyalty/gift-cards/${id}/deactivate`, undefined, token);
  expect(result.status).toBe(200);
}

/** Digits of a money text such as "৳1,060" or "-৳150" as a number. */
export function amountOf(text: string): number {
  const bn = "০১২৩৪৫৬৭৮৯";
  const ascii = text.replace(/[০-৯]/g, (d) => String(bn.indexOf(d)));
  return Number(ascii.replace(/[^\d.]/g, ""));
}
