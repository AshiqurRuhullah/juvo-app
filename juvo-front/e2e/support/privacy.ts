import { call, nextVariant } from "./cod";
import { adminAccess, customerAccess } from "./returns";

/** Helpers of the privacy specs: data made through the real customer and staff APIs. */

let districtId: string | null = null;
async function dhaka(): Promise<string> {
  if (districtId) return districtId;
  const list = await call<{ id: string; nameEn: string }[]>("GET", "/api/v1/store/geo/districts", { expect: 200 });
  districtId = list.json.find((d) => d.nameEn === "Dhaka")!.id;
  return districtId;
}

/** Places a cash-on-delivery order for this mobile and leaves it open (status Placed), so the customer cannot be anonymised yet. */
export async function placeOpenOrder(mobile: string): Promise<{ orderId: string; number: string }> {
  const variant = await nextVariant();
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
  return { orderId: placed.json.orderId, number: placed.json.number };
}

/** The customer changes one consent through the shop API (the same call the profile page makes). */
export async function changeConsent(mobile: string, type: "MarketingSms" | "MarketingEmail", granted: boolean): Promise<void> {
  const token = await customerAccess(mobile);
  await call("PUT", "/api/v1/me/consent", { token, body: { type, granted }, expect: 200 });
}

/** The consent history as the staff API sends it (Super Admin). */
export async function consentHistory(customerId: string): Promise<{ items: { type: string; oldValue: boolean | null; newValue: boolean }[] }> {
  const result = await call<{ items: { type: string; oldValue: boolean | null; newValue: boolean }[] }>("GET", `/api/v1/customers/${customerId}/consents/history`, { token: await adminAccess(), expect: 200 });
  return result.json;
}

/** The customers the staff search finds for this mobile (the original and, after anonymisation, a new one). */
export async function customersByMobile(mobile: string): Promise<{ id: string; mobile: string }[]> {
  const found = await call<{ items: { id: string; mobile: string }[] }>("GET", `/api/v1/customers?Search=${encodeURIComponent(mobile.slice(1))}`, { token: await adminAccess(), expect: 200 });
  return found.json.items;
}
