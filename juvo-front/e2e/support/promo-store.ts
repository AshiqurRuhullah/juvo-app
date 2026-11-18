import { expect } from "@playwright/test";
import { API, loadState } from "./env";
import { call } from "./cod";
import { adminAccess } from "./returns";
import { makePng } from "./png";

/**
 * Data for the promotion and pre-order specs of the shop, made through the real API: a published product of our own (so a promotion or a
 * pre-order switch never changes the prices or stock other specs rely on), promotions of every kind and pre-order settings. Every call
 * goes straight to the API, not through the browser.
 */
type Json = Record<string, unknown>;

export const stamp = () => `${Date.now().toString(36)}${Math.floor(Math.random() * 1296).toString(36)}`;

export type ShopProduct = { id: string; slug: string; name: string; sku: string; variantId: string; price: number };

/** A published product with one variant, a real picture and `stock` units at head office (0 = out of stock everywhere). */
export async function makeShopProduct(options: { label: string; price?: number; stock?: number; withSecondVariant?: boolean }): Promise<ShopProduct> {
  const token = await adminAccess();
  const tag = stamp();
  const name = `E2E ${options.label} ${tag}`;
  const price = options.price ?? 1000;
  const tree = await call<{ id: string }[]>("GET", "/api/v1/catalog/categories", { token, expect: 200 });
  const created = await call<{ id: string; slug: string }>("POST", "/api/v1/catalog/products", {
    token,
    body: { nameEn: name, categoryId: tree.json[0]!.id, regularPrice: price, targetGroup: "Unisex", isNew: false, isFeatured: false, isBestSeller: false },
    expect: 201,
  });
  const sku = `E2E-${options.label}-${tag}`.toUpperCase().replace(/[^A-Z0-9-]/g, "");
  const variant = await call<{ id: string }>("POST", `/api/v1/catalog/products/${created.json.id}/variants`, { token, body: { sku, colorName: "Red", size: "M" }, expect: 201 });
  if (options.withSecondVariant) await call("POST", `/api/v1/catalog/products/${created.json.id}/variants`, { token, body: { sku: `${sku}-L`, colorName: "Red", size: "L" }, expect: 201 });

  const form = new FormData();
  form.append("file", new Blob([new Uint8Array(makePng(1000, 1250))], { type: "image/png" }), "front.png");
  form.append("altText", name);
  const upload = await fetch(`${API}/api/v1/catalog/products/${created.json.id}/images`, { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: form });
  expect(upload.status, await upload.text()).toBe(201);

  if ((options.stock ?? 0) > 0) {
    await call("POST", "/api/v1/inventory/adjustments", { token, body: { branchId: loadState().branches.ho.id, reason: "OpeningStock", lines: [{ variantId: variant.json.id, quantityChange: options.stock }] }, expect: 200 });
  }
  await call("POST", `/api/v1/catalog/products/${created.json.id}/publish`, { token, expect: 200 });
  const detail = await call<{ slug: string }>("GET", `/api/v1/catalog/products/${created.json.id}`, { token, expect: 200 });
  return { id: created.json.id, slug: detail.json.slug ?? created.json.slug, name, sku, variantId: variant.json.id, price };
}

/** Switch pre-orders on for a product (and optionally set the date and the advance percent). */
export async function allowPreorder(productId: string, options: { date?: string | null; advancePercent?: number | null } = {}) {
  const token = await adminAccess();
  await call("PUT", `/api/v1/catalog/products/${productId}/preorder`, { token, body: { allowPreorder: true, expectedShipDate: options.date ?? null, advancePercent: options.advancePercent ?? null }, expect: 200 });
}

export type PromotionInput = Json & { nameEn: string; type: string };

/** Creates a promotion as a draft and switches it on. `endsInMs` / `startsInMs` are relative to now (Dhaka offset in the text). */
export async function makePromotion(input: PromotionInput, options: { startsInMs?: number; endsInMs?: number | null; activate?: boolean } = {}): Promise<{ id: string }> {
  const token = await adminAccess();
  const iso = (ms: number) => new Date(Date.now() + ms).toISOString();
  const body = {
    stackMode: "StacksWithOrderPromotions",
    channels: "Web, SocialDesk",
    priority: 5,
    startsAt: iso(options.startsInMs ?? -60_000),
    endsAt: options.endsInMs === null ? null : iso(options.endsInMs ?? 3_600_000),
    labelEn: input.nameEn,
    labelBn: input.nameEn,
    targets: [],
    ...input,
  };
  const created = await call<{ id: string }>("POST", "/api/v1/promotions", { token, body, expect: 201 });
  if (options.activate !== false) await call("POST", `/api/v1/promotions/${created.json.id}/activate`, { token, expect: 200 });
  return created.json;
}

export async function archivePromotion(id: string) {
  const token = await adminAccess();
  await call("POST", `/api/v1/promotions/${id}/archive`, { token });
}

export const target = (productId: string, role = "Main") => ({ kind: "Product", targetId: productId, role, quantity: 1 });

/** A flash sale of `percent` on one product ending `endsInMs` from now. */
export function flashSale(product: ShopProduct, percent: number, endsInMs: number, name = `Flash ${stamp()}`) {
  return makePromotion({ nameEn: name, type: "FlashSale", discountKind: "Percent", discountValue: percent, targets: [target(product.id)] }, { endsInMs });
}

/** A staff user with one default role at head office (created with a temporary password, then moved to a permanent one like a real first sign-in). */
export async function makeStaff(roleName: string): Promise<{ email: string; password: string }> {
  const token = await adminAccess();
  const roles = await call<{ items: { id: string; name: string }[] }>("GET", "/api/v1/ums/roles?pageSize=50", { token, expect: 200 });
  const roleId = roles.json.items.find((r) => r.name === roleName)!.id;
  const email = `${roleName.toLowerCase().replace(/[^a-z]/g, "")}.${stamp()}@e2e.test`;
  const created = await call<{ temporaryPassword: string }>("POST", "/api/v1/ums/users", { token, body: { email, fullName: `E2E ${roleName}`, assignments: [{ branchId: loadState().branches.ho.id, roleId }] }, expect: 201 });
  const password = "E2e-Staff#Pass1";
  const limited = await call<{ accessToken: string }>("POST", "/api/v1/auth/login", { body: { identifier: email, password: created.json.temporaryPassword }, expect: 200 });
  await call("POST", "/api/v1/auth/change-password", { token: limited.json.accessToken, body: { currentPassword: created.json.temporaryPassword, newPassword: password }, expect: 200 });
  return { email, password };
}

/** Puts stock into a branch for a variant (a real stock adjustment, so purchase receipts and the ledger stay honest). */
export async function addStock(branchId: string, variantId: string, quantity: number) {
  const token = await adminAccess();
  await call("POST", "/api/v1/inventory/adjustments", { token, body: { branchId, reason: "OpeningStock", lines: [{ variantId, quantityChange: quantity }] }, expect: 200 });
}

/** Uploads the hero or banner picture of a campaign (a real PNG, long side 1250 px). */
export async function uploadPromoImage(promotionId: string, kind: "hero" | "banner") {
  const token = await adminAccess();
  const form = new FormData();
  form.append("file", new Blob([new Uint8Array(makePng(1250, 1000, [200, 30, 30], [240, 200, 40]))], { type: "image/png" }), `${kind}.png`);
  const response = await fetch(`${API}/api/v1/promotions/${promotionId}/images/${kind}`, { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: form });
  expect(response.status, await response.text()).toBeLessThan(300);
}
