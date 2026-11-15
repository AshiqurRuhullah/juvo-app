import { expect, type APIRequestContext, type PlaywrightWorkerArgs } from "@playwright/test";
import { E2E } from "../../playwright.config";
import { api } from "./env";
import { apiCheckout, type Placed } from "./orders";

/**
 * Data for the courier booking and live-update specs, made through the real API: an order is placed as a guest (through the BFF, like
 * the shop), then staff confirm and pack it. Parcels are booked and advanced with the staff API.
 */
type Playwright = PlaywrightWorkerArgs["playwright"];

export type Parcel = { id: string; consignmentId: string; status: string; courierId: string };

/** A request context with its own cookie jar: one guest with one cart. */
export async function guestContext(playwright: Playwright): Promise<APIRequestContext> {
  return playwright.request.newContext({ baseURL: E2E.webUrl });
}

/** Places a cash on delivery order for one unit of the variant and confirms it, so it is ready to pack. */
export async function confirmedOrder(playwright: Playwright, admin: string, variantId: string, quantity = 1): Promise<Placed> {
  const request = await guestContext(playwright);
  try {
    const placed = await apiCheckout(request, { lines: [{ variantId, quantity }] });
    const confirm = await api("POST", `/api/v1/orders/${placed.orderId}/confirm`, { advanceReceived: true }, admin);
    expect(confirm.status, JSON.stringify(confirm.json)).toBe(200);
    return placed;
  } finally {
    await request.dispose();
  }
}

/** The same, packed: the order is Packed and waits for a courier. */
export async function packedOrder(playwright: Playwright, admin: string, variantId: string, quantity = 1): Promise<Placed> {
  const placed = await confirmedOrder(playwright, admin, variantId, quantity);
  const pack = await api("POST", `/api/v1/orders/${placed.orderId}/pack`, undefined, admin);
  expect(pack.status, JSON.stringify(pack.json)).toBe(200);
  return placed;
}

/** Books the order with the best eligible courier (the sandbox courier in the e2e database). */
export async function bookOrder(admin: string, orderId: string): Promise<Parcel> {
  const booked = await api<Parcel>("POST", `/api/v1/shipments/orders/${orderId}/book`, {}, admin);
  expect(booked.status, JSON.stringify(booked.json)).toBe(201);
  return booked.json;
}

/** Moves a sandbox parcel to its next step (or to the named raw status, for example `delivery_failed`). */
export async function advanceParcel(admin: string, parcelId: string, status?: string): Promise<Parcel> {
  const moved = await api<Parcel>("POST", `/api/v1/shipments/${parcelId}/advance`, { status: status ?? null, note: null }, admin);
  expect(moved.status, JSON.stringify(moved.json)).toBe(200);
  return moved.json;
}

/** The tracking page address with the private token of a placed order. */
export const trackUrl = (placed: Placed) => `/track?token=${encodeURIComponent(placed.trackingToken!)}`;
