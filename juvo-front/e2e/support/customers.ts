import { call } from "./cod";
import { adminAccess, freshMobile } from "./returns";

/** Helpers of the customer-admin specs: customers made through the real customer API (mobile + sandbox code), read back through the staff API. */

type Json = Record<string, unknown>;

export type CustomerRef = { id: string; mobile: string; name: string; email: string };

/** The unique part of a mobile (without the leading 0) so the staff search finds exactly one customer. */
export const searchKey = (mobile: string) => mobile.slice(1);

async function customerToken(mobile: string): Promise<string> {
  const requested = await call<{ devCode: string }>("POST", "/api/v1/auth/otp/request", { body: { mobile, purpose: null, channel: "sms", email: null }, expect: 202 });
  const verified = await call<{ accessToken: string }>("POST", "/api/v1/auth/otp/verify", { body: { mobile, code: requested.json.devCode, deviceName: "e2e" }, expect: 200 });
  return verified.json.accessToken;
}

/** A customer with a name, an email, one saved address and (optionally) a marketing SMS consent. */
export async function makeCustomer(options: { name?: string; withAddress?: boolean; marketingSms?: boolean } = {}): Promise<CustomerRef> {
  const mobile = freshMobile();
  const suffix = Math.random().toString(36).slice(2, 8);
  const name = options.name ?? `Rina ${suffix}`;
  const email = `rina.${suffix}@example.com`;
  const token = await customerToken(mobile);
  await call("PUT", "/api/v1/me", { token, body: { fullName: name, email }, expect: 200 });
  if (options.withAddress !== false) {
    const districts = await call<{ id: string; nameEn: string }[]>("GET", "/api/v1/store/geo/districts", { expect: 200 });
    await call("POST", "/api/v1/me/addresses", {
      token,
      body: { label: "Home", recipientName: name, phone: mobile, countryCode: "BD", districtId: districts.json.find((d) => d.nameEn === "Dhaka")!.id, area: "Dhanmondi", addressLine: "House 12, Road 5", isDefault: true },
      expect: 201,
    });
  }
  if (options.marketingSms) await call("PUT", "/api/v1/me/consent", { token, body: { type: "MarketingSms", granted: true }, expect: 200 });
  const found = await call<{ items: { id: string }[] }>("GET", `/api/v1/customers?Search=${encodeURIComponent(searchKey(mobile))}`, { token: await adminAccess(), expect: 200 });
  return { id: found.json.items[0]!.id, mobile, name, email };
}

/** One customer as the staff API shows it (as Super Admin: unmasked). */
export async function readCustomer(id: string): Promise<{ customer: { isActive: boolean; profileLevel: string; mobile: string } } & Json> {
  const result = await call<{ customer: { isActive: boolean; profileLevel: string; mobile: string } }>("GET", `/api/v1/customers/${id}`, { token: await adminAccess(), expect: 200 });
  return result.json;
}
