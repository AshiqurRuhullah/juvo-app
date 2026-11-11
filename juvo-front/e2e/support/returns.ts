import { expect } from "@playwright/test";
import { API, loadState, sql } from "./env";

/**
 * Data for the returns specs, made through the real API (the e2e database starts without orders): a cash-on-delivery order is placed
 * as a guest, then staff confirm, pack and deliver it. Every call goes straight to the API, not through the browser.
 */

type Json = Record<string, unknown>;

async function call<T = Json>(method: string, path: string, options: { body?: unknown; token?: string; cartToken?: string; expect?: number } = {}): Promise<{ status: number; json: T }> {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: {
      "Content-Type": "application/json",
      ...(options.token ? { Authorization: `Bearer ${options.token}` } : {}),
      ...(options.cartToken ? { "X-Cart-Token": options.cartToken } : {}),
    },
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
  });
  const text = await response.text();
  const json = (text ? JSON.parse(text) : undefined) as T;
  if (options.expect !== undefined) expect(response.status, `${method} ${path}: ${text}`).toBe(options.expect);
  return { status: response.status, json };
}

let adminToken: { token: string; at: number } | null = null;

/** A Super Admin access token (kept for 10 minutes; the access token lives 15). */
export async function adminAccess(): Promise<string> {
  if (adminToken && Date.now() - adminToken.at < 10 * 60_000) return adminToken.token;
  const admin = loadState().admin;
  const login = await call<{ accessToken: string }>("POST", "/api/v1/auth/login", { body: { identifier: admin.email, password: admin.password }, expect: 200 });
  adminToken = { token: login.json.accessToken, at: Date.now() };
  return adminToken.token;
}

/** Signs a staff user in directly on the API. */
export async function staffAccess(email: string, password: string): Promise<string> {
  const login = await call<{ accessToken: string }>("POST", "/api/v1/auth/login", { body: { identifier: email, password }, expect: 200 });
  return login.json.accessToken;
}

let districtId: string | null = null;
async function dhaka(): Promise<string> {
  if (districtId) return districtId;
  const list = await call<{ id: string; nameEn: string }[]>("GET", "/api/v1/store/geo/districts", { expect: 200 });
  districtId = list.json.find((d) => d.nameEn === "Dhaka")!.id;
  return districtId;
}

export type SampleVariant = { id: string; productId: string; slug: string; sku: string; price: number; available: number };

/** In-stock variants of the sample catalogue (one per product, at least `minAvailable` units), straight from the public API. */
export async function stockedVariants(minAvailable: number, count: number, skip = 0): Promise<SampleVariant[]> {
  const list = await call<{ items: { slug: string }[] }>("GET", "/api/v1/store/products?pageSize=24", { expect: 200 });
  const found: SampleVariant[] = [];
  for (const item of list.json.items) {
    const detail = await call<{ card: { id: string }; variants: { id: string; sku: string; price: number | string; available: number | string; inStock: boolean }[] }>("GET", `/api/v1/store/products/${item.slug}`);
    if (detail.status !== 200) continue;
    const variant = detail.json.variants.find((v) => v.inStock && Number(v.available) >= minAvailable);
    if (!variant) continue;
    found.push({ id: variant.id, productId: detail.json.card.id, slug: item.slug, sku: variant.sku, price: Number(variant.price), available: Number(variant.available) });
  }
  expect(found.length, "the sample catalogue needs more in-stock products").toBeGreaterThanOrEqual(skip + count);
  return found.slice(skip, skip + count);
}

/** A product with at least two in-stock variants (for the exchange): the bought variant and a different one to swap to. */
export async function productWithTwoVariants(): Promise<{ bought: SampleVariant; other: { id: string; sku: string } }> {
  const list = await call<{ items: { slug: string }[] }>("GET", "/api/v1/store/products?pageSize=24", { expect: 200 });
  for (const item of list.json.items) {
    const detail = await call<{ card: { id: string }; variants: { id: string; sku: string; price: number | string; available: number | string; inStock: boolean }[] }>("GET", `/api/v1/store/products/${item.slug}`);
    if (detail.status !== 200) continue;
    const stocked = detail.json.variants.filter((v) => v.inStock && Number(v.available) >= 2);
    if (stocked.length < 2) continue;
    const [a, b] = stocked;
    return { bought: { id: a!.id, productId: detail.json.card.id, slug: item.slug, sku: a!.sku, price: Number(a!.price), available: Number(a!.available) }, other: { id: b!.id, sku: b!.sku } };
  }
  throw new Error("the sample catalogue has no product with two in-stock variants");
}

let mobileCounter = 0;
/** A fresh Bangladesh mobile number (unique inside one run). */
export function freshMobile(): string {
  mobileCounter += 1;
  return "018" + String(Math.floor(10_000_000 + Math.random() * 80_000_000) + mobileCounter).padStart(8, "0");
}

export type Delivered = { orderId: string; number: string; mobile: string; lines: { variantId: string; quantity: number }[] };

/** Places, confirms, packs and delivers a cash-on-delivery order of the given lines. */
export async function deliveredOrder(lines: { variantId: string; quantity: number }[], mobile = freshMobile()): Promise<Delivered> {
  const admin = await adminAccess();
  let cartToken = "";
  for (const line of lines) {
    const cart = await call<{ token?: string }>("POST", "/api/v1/store/cart/items", { body: line, cartToken: cartToken || undefined, expect: 200 });
    cartToken = cart.json.token ?? cartToken;
  }
  const otp = await call<{ devCode: string }>("POST", "/api/v1/auth/otp/request", { body: { mobile, purpose: "ConfirmOrder" }, expect: 202 });
  const placed = await call<{ orderId: string; number: string }>("POST", "/api/v1/store/checkout", {
    cartToken,
    expect: 201,
    body: {
      contact: { mobile, fullName: "Nadia Rahman" },
      address: { recipientName: "Nadia Rahman", countryCode: "BD", districtId: await dhaka(), addressLine: "House 12, Road 5, Dhanmondi" },
      paymentMethod: "COD",
      acceptTerms: true,
      otpCode: otp.json.devCode,
    },
  });
  const id = placed.json.orderId;
  // Several orders from one device and address in a short time can be held for a fraud review: staff approve the hold first.
  // A large cash order may also need its delivery-charge advance, which staff mark as received.
  let confirm = await call<{ title?: string }>("POST", `/api/v1/orders/${id}/confirm`, { token: admin, body: {} });
  if (confirm.status === 409 && /fraud/i.test(confirm.json.title ?? "")) {
    await call("POST", `/api/v1/fraud/reviews/${id}/decide`, { token: admin, body: { action: "Approve", note: "e2e" }, expect: 200 });
    confirm = await call("POST", `/api/v1/orders/${id}/confirm`, { token: admin, body: {} });
  }
  if (confirm.status === 409 && /advance/i.test(confirm.json.title ?? "")) {
    confirm = await call("POST", `/api/v1/orders/${id}/confirm`, { token: admin, body: { advanceReceived: true } });
  }
  expect(confirm.status, JSON.stringify(confirm.json)).toBe(200);
  await call("POST", `/api/v1/orders/${id}/pack`, { token: admin, body: {}, expect: 200 });
  for (const to of ["HandedToCourier", "InTransit", "OutForDelivery", "Delivered"]) {
    await call("POST", `/api/v1/orders/${id}/transition`, { token: admin, body: { to }, expect: 200 });
  }
  return { orderId: id, number: placed.json.number, mobile, lines };
}

/** Moves the delivery date back so the return period (7 days) has ended. */
export function expireReturnWindow(orderId: string) {
  sql(`SET QUOTED_IDENTIFIER ON; UPDATE Orders SET DeliveredAt = DATEADD(day, -30, SYSDATETIMEOFFSET()) WHERE Id = '${orderId}'`);
}

/** A customer access token for this mobile (same identity the order was placed with). */
export async function customerAccess(mobile: string): Promise<string> {
  const requested = await call<{ devCode: string }>("POST", "/api/v1/auth/otp/request", { body: { mobile, purpose: null, channel: "sms", email: null }, expect: 202 });
  const verified = await call<{ accessToken: string }>("POST", "/api/v1/auth/otp/verify", { body: { mobile, code: requested.json.devCode, deviceName: "e2e" }, expect: 200 });
  return verified.json.accessToken;
}

export type ReturnRef = { id: string; number: string };

/** Files a return request as the customer (through the API) for the first line of the order. */
export async function requestReturn(order: Delivered, options: { quantity?: number; kind?: "Return" | "Exchange"; reasonCode?: string; exchangeVariantId?: string | null } = {}): Promise<ReturnRef> {
  const token = await customerAccess(order.mobile);
  const eligibility = await call<{ items: { orderItemId: string }[] }>("GET", `/api/v1/me/orders/${order.orderId}/return-eligibility`, { token, expect: 200 });
  const created = await call<{ id: string; number: string }>("POST", "/api/v1/me/returns", {
    token,
    expect: 201,
    body: {
      orderId: order.orderId,
      kind: options.kind ?? "Return",
      reasonCode: options.reasonCode ?? "SIZE_FIT",
      details: "Does not fit",
      items: [{ orderItemId: eligibility.json.items[0]!.orderItemId, quantity: options.quantity ?? 1, exchangeVariantId: options.exchangeVariantId ?? null }],
      photoPaths: [],
    },
  });
  return { id: created.json.id, number: created.json.number };
}

/** One staff step on a return (approve, picked-up, ...), as Super Admin by default. */
export async function staffStep(returnId: string, action: string, body: unknown = {}, token?: string): Promise<Json> {
  const result = await call("POST", `/api/v1/returns/${returnId}/${action}`, { token: token ?? (await adminAccess()), body, expect: 200 });
  return result.json;
}

/** Walks a return from Requested to the given stage through the staff API. */
export async function advanceTo(returnId: string, stage: "Approved" | "PickupScheduled" | "PickedUp" | "Received" | "Inspected"): Promise<void> {
  const order = ["Approved", "PickupScheduled", "PickedUp", "Received", "Inspected"];
  const goal = order.indexOf(stage);
  const today = new Date();
  const date = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, "0")}-${String(today.getDate()).padStart(2, "0")}`;
  if (goal >= 0) await staffStep(returnId, "approve");
  if (goal >= 1) await staffStep(returnId, "schedule-pickup", { date, courier: "Pathao", note: "Ring the bell" });
  if (goal >= 2) await staffStep(returnId, "picked-up");
  if (goal >= 3) await staffStep(returnId, "received");
  if (goal >= 4) {
    const detail = (await call<{ items: { id: string }[] }>("GET", `/api/v1/returns/${returnId}`, { token: await adminAccess(), expect: 200 })).json;
    await staffStep(returnId, "inspect", { items: detail.items.map((i) => ({ returnItemId: i.id, condition: "Good", restock: true, acceptRefund: true })), fault: "Customer", note: null });
  }
}

/** A staff user with a role that holds exactly these permissions in the head office (created through the admin API). */
export async function staffWithPermissions(permissions: string[]): Promise<{ email: string; password: string }> {
  const admin = await adminAccess();
  const suffix = Math.random().toString(36).slice(2, 8);
  const role = await call<{ id: string }>("POST", "/api/v1/ums/roles", { token: admin, body: { name: `Returns ${suffix}`, description: "returns e2e", permissions }, expect: 201 });
  const email = `ret-${suffix}@e2e.test`;
  const created = await call<{ user: { id: string }; temporaryPassword: string }>("POST", "/api/v1/ums/users", {
    token: admin,
    body: { email, fullName: `Returns ${suffix}`, assignments: [{ branchId: loadState().branches.ho.id, roleId: role.json.id }] },
    expect: 201,
  });
  const limited = await call<{ accessToken: string }>("POST", "/api/v1/auth/login", { body: { identifier: email, password: created.json.temporaryPassword }, expect: 200 });
  const password = "E2e-Staff#Pass1";
  await call("POST", "/api/v1/auth/change-password", { token: limited.json.accessToken, body: { currentPassword: created.json.temporaryPassword, newPassword: password }, expect: 200 });
  return { email, password };
}
