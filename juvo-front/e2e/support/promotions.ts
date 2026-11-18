import { expect } from "@playwright/test";
import { api } from "./env";
import { adminAccess } from "./returns";

/** Helpers for the promotions specs: promotions are made and read straight on the API (the browser tests then work on them). */

export const uniqueName = (prefix: string) => `${prefix} ${Math.random().toString(36).slice(2, 8)}`;

/** `YYYY-MM-DDTHH:mm` on the Dhaka clock, `hours` from now: the value of a datetime-local field (times are typed as Dhaka time). */
export function dhakaLocalIn(hours: number): string {
  return new Date(Date.now() + 6 * 3600_000 + hours * 3600_000).toISOString().slice(0, 16);
}

export type PromotionRef = { id: string; nameEn: string; status: string };

export async function createPromotion(body: Record<string, unknown>, expectStatus = 201): Promise<PromotionRef> {
  const token = await adminAccess();
  const response = await api<PromotionRef>("POST", "/api/v1/promotions", body, token);
  expect(response.status, JSON.stringify(response.json)).toBe(expectStatus);
  return response.json;
}

export async function promotionAction(id: string, action: "activate" | "deactivate" | "archive" | "duplicate"): Promise<PromotionRef> {
  const token = await adminAccess();
  const response = await api<PromotionRef>("POST", `/api/v1/promotions/${id}/${action}`, undefined, token);
  expect(response.status, JSON.stringify(response.json)).toBe(200);
  return response.json;
}

export type PromotionDetailJson = { id: string; nameEn: string; status: string; startsAt: string | null; endsAt: string | null; type: string; priority: number; channels: string; stackMode: string; discountValue: number | null; targets: { kind: string; role: string; targetId: string | null; quantity: number }[]; heroImageUrl: string | null; slug: string | null };

export async function getPromotion(id: string): Promise<PromotionDetailJson> {
  const token = await adminAccess();
  const response = await api<PromotionDetailJson>("GET", `/api/v1/promotions/${id}`, undefined, token);
  expect(response.status).toBe(200);
  return response.json;
}

export async function findPromotionByName(name: string): Promise<PromotionDetailJson | null> {
  const token = await adminAccess();
  const list = await api<{ items: { id: string; nameEn: string }[] }>("GET", `/api/v1/promotions?Page=1&PageSize=20&Search=${encodeURIComponent(name)}`, undefined, token);
  const hit = list.json.items.find((p) => p.nameEn === name);
  return hit ? getPromotion(hit.id) : null;
}

export async function reportOf(id: string): Promise<{ orders: number; units: number; revenue: number; discountCost: number }> {
  const token = await adminAccess();
  const response = await api<{ orders: number; units: number; revenue: number; discountCost: number }>("GET", `/api/v1/promotions/${id}/report`, undefined, token);
  expect(response.status).toBe(200);
  return response.json;
}

/** A published product of the sample catalogue whose name no other product shares (the picker finds it by name). */
export async function uniqueProduct(skip = 0): Promise<{ id: string; nameEn: string; variants: { id: string; sku: string }[] }> {
  const token = await adminAccess();
  const list = await api<{ items: { id: string; nameEn: string }[] }>("GET", "/api/v1/catalog/products?Page=1&PageSize=60&status=Published", undefined, token);
  const found: { id: string; nameEn: string; variants: { id: string; sku: string }[] }[] = [];
  for (const item of list.json.items) {
    if (list.json.items.filter((x) => x.nameEn === item.nameEn).length !== 1) continue;
    const detail = await api<{ id: string; nameEn: string; variants: { id: string; sku: string; isActive: boolean; tracksStock: boolean }[] }>("GET", `/api/v1/catalog/products/${item.id}`, undefined, token);
    if (detail.status !== 200) continue;
    const variants = detail.json.variants.filter((v) => v.isActive && v.tracksStock).map((v) => ({ id: v.id, sku: v.sku }));
    if (variants.length >= 1) found.push({ id: detail.json.id, nameEn: detail.json.nameEn, variants });
  }
  expect(found.length, "the sample catalogue needs published products").toBeGreaterThan(skip);
  return found[skip]!;
}
