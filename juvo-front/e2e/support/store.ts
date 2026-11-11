import { expect, type APIRequestContext, type BrowserContext, type Locator, type Page } from "@playwright/test";
import { api, loadState } from "./env";

/** The first in-stock variants of the sample catalogue (id and price), one per product. */
export async function sampleVariants(request: APIRequestContext, count: number): Promise<{ id: string; price: number; slug: string; productId: string }[]> {
  const list = await request.get("/api/bff/api/v1/store/products?pageSize=24");
  expect(list.status()).toBe(200);
  const items = ((await list.json()) as { items: { slug: string; id?: string; productId?: string }[] }).items;
  const found: { id: string; price: number; slug: string; productId: string }[] = [];
  for (const item of items) {
    const detail = await request.get(`/api/bff/api/v1/store/products/${item.slug}`);
    if (detail.status() !== 200) continue;
    const body = (await detail.json()) as { card: { id?: string; productId?: string }; variants: { id: string; price: number | string; inStock: boolean; available: boolean }[] };
    const variant = body.variants.find((v) => v.inStock && v.available);
    if (!variant) continue;
    found.push({ id: variant.id, price: Number(variant.price), slug: item.slug, productId: (body.card.id ?? body.card.productId ?? item.id ?? item.productId)! });
    if (found.length === count) break;
  }
  expect(found.length, "the sample catalogue needs enough in-stock products").toBe(count);
  return found;
}

export async function addToCart(context: BrowserContext, variantId: string, quantity = 1) {
  const response = await context.request.post("/api/bff/api/v1/store/cart/items", { data: { variantId, quantity } });
  expect(response.status(), await response.text()).toBeLessThan(300);
}

/** A fresh Bangladesh mobile number for each test. */
export const newMobile = () => "017" + Math.floor(10_000_000 + Math.random() * 89_999_999);

/** Signs a customer in through the BFF (the API is in sandbox mode and returns the code), which also merges a guest cart. */
export async function signInCustomer(context: BrowserContext, mobile = newMobile()): Promise<string> {
  const requested = await context.request.post("/api/bff/api/v1/auth/otp/request", { data: { mobile, purpose: null, channel: "sms", email: null } });
  expect(requested.status(), await requested.text()).toBe(202);
  const { devCode } = (await requested.json()) as { devCode: string | null };
  expect(devCode, "the e2e API must show the sandbox code").toBeTruthy();
  const verified = await context.request.post("/api/bff/auth/otp/verify", { data: { mobile, code: devCode, deviceName: "e2e" } });
  expect(verified.status(), await verified.text()).toBe(200);
  return mobile;
}

/** Creates a coupon as the admin (direct API call) and returns its code. */
export async function createCoupon(kind: "Percent" | "Fixed", value: number, minOrderValue = 0): Promise<string> {
  const admin = loadState().admin;
  const login = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: admin.email, password: admin.password });
  expect(login.status).toBe(200);
  const code = "E2E" + Math.random().toString(36).slice(2, 8).toUpperCase();
  const created = await api("POST", "/api/v1/promotions/coupons", { code, description: "E2E coupon", kind, value, minOrderValue, maxDiscount: null, startsAt: null, endsAt: null, usageLimit: null, isActive: true }, login.json.accessToken);
  expect(created.status, JSON.stringify(created.json)).toBe(201);
  return code;
}

/** Opens a select, picks an option by name and waits until the list has closed (the next select may then load its options). */
export async function pick(page: Page, trigger: Locator, name: string) {
  await trigger.click();
  await page.getByRole("option", { name }).first().click();
  await expect(page.getByRole("option")).toHaveCount(0);
}
