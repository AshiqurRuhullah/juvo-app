import { expect, type PlaywrightWorkerArgs } from "@playwright/test";
import { E2E } from "../../playwright.config";
import { call } from "./cod";
import { loadState } from "./env";
import { apiCheckout, disableAllRules, staffToken, topUpHeadOfficeStock, type Placed } from "./orders";
import { adminAccess } from "./returns";
import { sampleVariants } from "./store";

/** Helpers of the coupon specs: coupons are made and read straight on the API, and a real order uses one through the shop's own endpoints. */

export type CouponApi = {
  id: string; code: string; description: string | null; kind: "Percent" | "Fixed"; value: number; minOrderValue: number; maxDiscount: number | null;
  startsAt: string | null; endsAt: string | null; usageLimit: number | null; usedCount: number; isActive: boolean; stacksWithPromotions: boolean;
};

/** A code no other run holds: the prefix, then 6 random capitals and digits. */
export const newCode = (prefix = "E2C") => `${prefix}${Math.random().toString(36).slice(2, 8).toUpperCase()}`;

/** `YYYY-MM-DDTHH:mm` on the Dhaka clock, `hours` from now (the value of a datetime-local field). */
export function dhakaLocalIn(hours: number): string {
  return new Date(Date.now() + 6 * 3600_000 + hours * 3600_000).toISOString().slice(0, 16);
}

const defaults = { description: null, kind: "Percent", value: 10, minOrderValue: 0, maxDiscount: null, startsAt: null, endsAt: null, usageLimit: null, isActive: true, stacksWithPromotions: false };

export async function makeCoupon(patch: Partial<Omit<CouponApi, "id" | "usedCount">> = {}): Promise<CouponApi> {
  const body = { ...defaults, code: newCode(), ...patch };
  const created = await call<CouponApi>("POST", "/api/v1/promotions/coupons", { token: await adminAccess(), body, expect: 201 });
  return created.json;
}

export async function findCoupon(code: string): Promise<CouponApi | null> {
  const list = await call<{ items: CouponApi[] }>("GET", `/api/v1/promotions/coupons?Page=1&PageSize=20&Search=${encodeURIComponent(code)}`, { token: await adminAccess(), expect: 200 });
  return list.json.items.find((c) => c.code === code) ?? null;
}

export async function readCoupon(id: string): Promise<CouponApi | null> {
  const response = await call<CouponApi>("GET", `/api/v1/promotions/coupons/${id}`, { token: await adminAccess() });
  return response.status === 200 ? response.json : null;
}

export async function removeCoupon(id: string) {
  await call("DELETE", `/api/v1/promotions/coupons/${id}`, { token: await adminAccess() });
}

/**
 * A real order that uses `code`: a fresh guest adds one sample item, attaches the coupon to the cart through the shop's endpoint and
 * checks out with cash on delivery. The order placement counts the use. Returns the order.
 */
export async function placeOrderWithCoupon(playwright: PlaywrightWorkerArgs["playwright"], code: string): Promise<Placed> {
  const admin = await staffToken("admin");
  await disableAllRules(admin);
  await topUpHeadOfficeStock(admin, loadState().branches.ho.id);
  const own = await playwright.request.newContext({ baseURL: E2E.webUrl });
  try {
    const [item] = await sampleVariants(own, 1);
    const added = await own.post("/api/bff/api/v1/store/cart/items", { data: { variantId: item!.id, quantity: 1 } });
    expect(added.status(), await added.text()).toBeLessThan(300);
    const attached = await own.post("/api/bff/api/v1/store/cart/coupon", { data: { code } });
    expect(attached.status(), await attached.text()).toBeLessThan(300);
    return await apiCheckout(own, { lines: [] });
  } finally {
    await own.dispose();
  }
}
