import { expect } from "@playwright/test";
import { API } from "./env";
import { adminAccess, freshMobile } from "./returns";

/**
 * Data for the courier and parcel specs, made through the real API: a cash-on-delivery order is placed as a guest, confirmed and packed
 * by staff, then booked with the sandbox courier. Every call goes straight to the API (not through the browser).
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

let district: string | null = null;
async function dhaka(): Promise<string> {
  if (district) return district;
  const list = await call<{ id: string; nameEn: string }[]>("GET", "/api/v1/store/geo/districts", { expect: 200 });
  district = list.json.find((d) => d.nameEn === "Dhaka")!.id;
  return district;
}

export type PackedOrder = { orderId: string; number: string; mobile: string; total: number };

/** Places, confirms and packs a cash-on-delivery order for the first variants given (quantity 1 each). */
export async function packedOrder(variantIds: string[], recipient = "Nadia Rahman"): Promise<PackedOrder> {
  const admin = await adminAccess();
  const mobile = freshMobile();
  let cartToken = "";
  for (const variantId of variantIds) {
    const cart = await call<{ token?: string }>("POST", "/api/v1/store/cart/items", { body: { variantId, quantity: 1 }, cartToken: cartToken || undefined, expect: 200 });
    cartToken = cart.json.token ?? cartToken;
  }
  const otp = await call<{ devCode: string }>("POST", "/api/v1/auth/otp/request", { body: { mobile, purpose: "ConfirmOrder" }, expect: 202 });
  const placed = await call<{ orderId: string; number: string; grandTotal?: number | string }>("POST", "/api/v1/store/checkout", {
    cartToken,
    expect: 201,
    body: {
      contact: { mobile, fullName: recipient },
      address: { recipientName: recipient, countryCode: "BD", districtId: await dhaka(), addressLine: "House 12, Road 5, Dhanmondi" },
      paymentMethod: "COD",
      acceptTerms: true,
      otpCode: otp.json.devCode,
    },
  });
  const id = placed.json.orderId;
  let confirm = await call<{ title?: string }>("POST", `/api/v1/orders/${id}/confirm`, { token: admin, body: {} });
  if (confirm.status === 409 && /fraud/i.test(confirm.json.title ?? "")) {
    await call("POST", `/api/v1/fraud/reviews/${id}/decide`, { token: admin, body: { action: "Approve", note: "e2e" }, expect: 200 });
    confirm = await call("POST", `/api/v1/orders/${id}/confirm`, { token: admin, body: {} });
  }
  if (confirm.status === 409 && /advance/i.test(confirm.json.title ?? "")) confirm = await call("POST", `/api/v1/orders/${id}/confirm`, { token: admin, body: { advanceReceived: true } });
  expect(confirm.status, JSON.stringify(confirm.json)).toBe(200);
  await call("POST", `/api/v1/orders/${id}/pack`, { token: admin, body: {}, expect: 200 });
  return { orderId: id, number: placed.json.number, mobile, total: Number(placed.json.grandTotal ?? 0) };
}

export type Parcel = { id: string; consignmentId: string; orderId: string; orderNumber: string; status: string; codAmount: number | string };

/** Books the order with the sandbox courier (or the one given) and answers the parcel. */
export async function bookParcel(orderId: string, body: Json = {}): Promise<Parcel> {
  const result = await call<Parcel>("POST", `/api/v1/shipments/orders/${orderId}/book`, { token: await adminAccess(), body, expect: 201 });
  return result.json;
}

/** A packed order that is booked: the most common starting point of a parcel test. */
export async function bookedParcel(variantIds: string[], recipient?: string): Promise<{ order: PackedOrder; parcel: Parcel }> {
  const order = await packedOrder(variantIds, recipient);
  return { order, parcel: await bookParcel(order.orderId) };
}

/** Moves a sandbox parcel along (the next step, or the given raw status such as `delivery_failed`). */
export async function advanceParcel(id: string, status?: string, note?: string): Promise<Parcel> {
  const result = await call<Parcel>("POST", `/api/v1/shipments/${id}/advance`, { token: await adminAccess(), body: { status: status ?? null, note: note ?? null }, expect: 200 });
  return result.json;
}

/** Cancels a booked parcel (API), for the list filters. */
export async function cancelParcel(id: string, reason = "e2e") {
  const result = await call("POST", `/api/v1/shipments/${id}/cancel`, { token: await adminAccess(), body: { reason } });
  expect(result.status, JSON.stringify(result.json)).toBeLessThan(300);
}

export async function sandboxCourierId(): Promise<string> {
  const list = await call<{ id: string; code: string }[]>("GET", "/api/v1/couriers", { token: await adminAccess(), expect: 200 });
  return list.json.find((c) => c.code === "SANDBOX")!.id;
}
