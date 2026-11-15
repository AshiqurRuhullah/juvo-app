import { expect } from "@playwright/test";
import { API } from "./env";
import { adminAccess, freshMobile, stockedVariants, type SampleVariant } from "./returns";

/**
 * Data for the COD settlement specs, made through the real API: a cash-on-delivery order is placed as a guest, staff confirm and pack
 * it, book the sandbox courier and advance the parcel to Delivered, so the payment is CollectedByCourier and the parcel is waiting for
 * a settlement batch. Every call goes straight to the API, not through the browser.
 */
type Json = Record<string, unknown>;

export async function call<T = Json>(method: string, path: string, options: { body?: unknown; token?: string; cartToken?: string; expect?: number } = {}): Promise<{ status: number; json: T }> {
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

let districtId: string | null = null;
async function dhaka(): Promise<string> {
  if (districtId) return districtId;
  const list = await call<{ id: string; nameEn: string }[]>("GET", "/api/v1/store/geo/districts", { expect: 200 });
  districtId = list.json.find((d) => d.nameEn === "Dhaka")!.id;
  return districtId;
}

let pool: SampleVariant[] | null = null;
let cursor = 0;
/** The next in-stock sample variant, cycling so one product's stock is not used up. */
export async function nextVariant(): Promise<SampleVariant> {
  pool ??= await stockedVariants(4, 8);
  const variant = pool[cursor % pool.length]!;
  cursor += 1;
  return variant;
}

export type Parcel = { orderId: string; orderNumber: string; shipmentId: string; consignmentId: string; courierId: string; branchId: string; expected: number };

/** A delivered cash-on-delivery parcel waiting for a batch. */
export async function deliveredParcel(): Promise<Parcel> {
  const admin = await adminAccess();
  const variant = await nextVariant();
  const mobile = freshMobile();
  const cart = await call<{ token?: string }>("POST", "/api/v1/store/cart/items", { body: { variantId: variant.id, quantity: 1 }, expect: 200 });
  const otp = await call<{ devCode: string }>("POST", "/api/v1/auth/otp/request", { body: { mobile, purpose: "ConfirmOrder" }, expect: 202 });
  const placed = await call<{ orderId: string; number: string }>("POST", "/api/v1/store/checkout", {
    cartToken: cart.json.token,
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
  const booked = await call<{ id: string }>("POST", `/api/v1/shipments/orders/${id}/book`, { token: admin, body: {}, expect: 201 });
  type Shipped = { id: string; consignmentId: string; courierId: string; branchId: string; codAmount: number | string; codCollectedAmount: number | string | null; status: string };
  let last: Shipped | null = null;
  for (let i = 0; i < 4; i += 1) {
    const step: { status: number; json: Shipped } = await call<Shipped>("POST", `/api/v1/shipments/${booked.json.id}/advance`, { token: admin, body: {}, expect: 200 });
    last = step.json;
  }
  expect(last!.status).toBe("Delivered");
  const amount = last!.codCollectedAmount ?? last!.codAmount;
  return { orderId: id, orderNumber: placed.json.number, shipmentId: last!.id, consignmentId: last!.consignmentId, courierId: last!.courierId, branchId: last!.branchId, expected: Number(amount) };
}

export type BatchRef = { id: string; number: string };

/** Makes a batch of exactly these parcels (so other waiting parcels never end up in it). */
export async function makeBatch(parcels: Parcel[], note: string | null = null): Promise<BatchRef> {
  const first = parcels[0]!;
  const made = await call<{ id: string; number: string }>("POST", "/api/v1/couriers/cod-settlements", {
    token: await adminAccess(),
    body: { courierId: first.courierId, branchId: first.branchId, shipmentIds: parcels.map((p) => p.shipmentId), note },
    expect: 201,
  });
  return { id: made.json.id, number: made.json.number };
}

/** Enters what the courier paid for each parcel (index order of `parcels`). */
export async function reconcileBatch(batch: BatchRef, parcels: Parcel[], received: (number | null)[], note = "e2e difference") {
  const lines = parcels.map((p, i) => ({ shipmentId: p.shipmentId, received: received[i] ?? p.expected, note }));
  await call("POST", `/api/v1/couriers/cod-settlements/${batch.id}/reconcile`, { token: await adminAccess(), body: { lines }, expect: 200 });
}

/** Money as the screens draw it, compared by digits only (separators and the taka sign differ by language). */
export const digits = (text: string | null) => Number((text ?? "").replace(/[^0-9.]/g, ""));
export const fmt2 = (n: number) => n.toFixed(2);
