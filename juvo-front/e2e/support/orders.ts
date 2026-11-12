import { expect, type APIRequestContext } from "@playwright/test";
import { api, loadState, sql } from "./env";
import { newMobile } from "./store";

export type Placed = { orderId: string; number: string; trackingToken: string | null; mobile: string; grandTotal: number; payNow: number };

/** The id of the Dhaka district (division and district are both called Dhaka in the Bangladesh data set). */
export async function dhakaDistrictId(request: APIRequestContext): Promise<string> {
  const divisions = (await (await request.get("/api/bff/api/v1/store/geo/divisions")).json()) as { id: string; nameEn: string }[];
  const dhaka = divisions.find((d) => d.nameEn === "Dhaka")!;
  const districts = (await (await request.get(`/api/bff/api/v1/store/geo/districts?divisionId=${dhaka.id}`)).json()) as { id: string; nameEn: string }[];
  return districts.find((d) => d.nameEn === "Dhaka")!.id;
}

/**
 * Places an order through the BFF exactly like the shop does (cart, sandbox code for a guest, checkout), without drawing any page.
 * A signed-in request context (customer cookies) needs no code. Use a fresh request context for every guest.
 */
type CheckoutOptions = { lines: { variantId: string; quantity: number }[]; mobile?: string; method?: "COD" | "SANDBOX" | "MANUAL"; signedIn?: boolean; confirmDuplicate?: boolean };

export async function apiCheckout(request: APIRequestContext, options: CheckoutOptions): Promise<Placed> {
  const mobile = options.mobile ?? newMobile();
  const response = await postCheckout(request, { ...options, mobile });
  expect(response.status(), await response.text()).toBe(201);
  const result = (await response.json()) as { orderId: string; number: string; trackingToken: string | null; grandTotal: number | string; payNow: number | string };
  return { orderId: result.orderId, number: result.number, trackingToken: result.trackingToken, mobile, grandTotal: Number(result.grandTotal), payNow: Number(result.payNow) };
}

/** The same call, but the answer is handed back as it is (to check a refusal). */
export async function postCheckout(request: APIRequestContext, options: CheckoutOptions & { mobile: string }) {
  const mobile = options.mobile;
  for (const line of options.lines) {
    const added = await request.post("/api/bff/api/v1/store/cart/items", { data: line });
    expect(added.status(), await added.text()).toBeLessThan(300);
  }
  let otpCode: string | null = null;
  if (!options.signedIn) {
    const requested = await request.post("/api/bff/api/v1/auth/otp/request", { data: { mobile, purpose: "ConfirmOrder", channel: "sms", email: null } });
    expect(requested.status(), await requested.text()).toBe(202);
    otpCode = ((await requested.json()) as { devCode: string | null }).devCode;
    expect(otpCode, "the e2e API must show the sandbox code").toBeTruthy();
  }
  const districtId = await dhakaDistrictId(request);
  return request.post("/api/bff/api/v1/store/checkout", {
    headers: { "Idempotency-Key": `e2e-${Date.now()}-${Math.random().toString(36).slice(2, 8)}` },
    data: {
      contact: { mobile, fullName: "E2E Buyer", email: null },
      addressId: null,
      address: { recipientName: "E2E Buyer", phone: mobile, countryCode: "BD", districtId, upazilaId: null, area: "Dhanmondi", addressLine: "House 1, Road 2", landmark: null, stateProvince: null, city: null, postalCode: null, latitude: null, longitude: null },
      saveAddress: false,
      paymentMethod: options.method ?? "COD",
      note: null,
      acceptTerms: true,
      otpCode,
      confirmDuplicate: options.confirmDuplicate ?? false,
      expectedTotal: null,
      idempotencyKey: null,
      deviceId: `e2e-device-${Math.random().toString(36).slice(2, 10)}`,
    },
  });
}

/** A staff access token for direct API calls (preparing data and checking results). */
export async function staffToken(who: "admin" | "manager" | "orderManager" = "admin"): Promise<string> {
  const user = loadState()[who];
  const login = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: user.email, password: user.password });
  expect(login.status).toBe(200);
  return login.json.accessToken;
}

type Rule = { code: string; enabled: boolean; threshold: number | string; action: string };

/** Changes one fraud rule (the e2e orders all come from one address, so the velocity rules would hold every later order). */
export async function setRule(token: string, code: string, patch: { enabled?: boolean; threshold?: number; action?: string }) {
  const rules = await api<Rule[]>("GET", "/api/v1/fraud/rules", undefined, token);
  const rule = rules.json.find((r) => r.code === code)!;
  const updated = await api("PUT", `/api/v1/fraud/rules/${code}`, { enabled: patch.enabled ?? rule.enabled, threshold: patch.threshold ?? Number(rule.threshold), action: patch.action ?? rule.action }, token);
  expect(updated.status, JSON.stringify(updated.json)).toBe(200);
}

/** Switches every fraud rule off, so ordinary test orders are never held. */
export async function disableAllRules(token: string) {
  const rules = await api<Rule[]>("GET", "/api/v1/fraud/rules", undefined, token);
  for (const rule of rules.json) if (rule.enabled) await setRule(token, rule.code, { enabled: false });
}

/** Creates a staff user (with a permanent password) the same way the setup project does. */
export async function createStaff(token: string, email: string, name: string, roleName: string, branchId: string): Promise<{ email: string; password: string }> {
  const password = "E2e-Staff#Pass1";
  const roles = await api<{ items: { id: string; name: string }[] }>("GET", "/api/v1/ums/roles?pageSize=50", undefined, token);
  const roleId = roles.json.items.find((r) => r.name === roleName)!.id;
  const created = await api<{ user: { id: string }; temporaryPassword: string }>("POST", "/api/v1/ums/users", { email, fullName: name, assignments: [{ branchId, roleId }] }, token);
  if (created.status === 409 || created.status === 400) return { email, password }; // already there from an earlier run of this spec
  expect(created.status, JSON.stringify(created.json)).toBe(201);
  const limited = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: email, password: created.json.temporaryPassword });
  const done = await api("POST", "/api/v1/auth/change-password", { currentPassword: created.json.temporaryPassword, newPassword: password }, limited.json.accessToken);
  expect(done.status).toBe(200);
  return { email, password };
}

type StockRow = { branchId: string; variantId: string; available: number | string };

/** Takes all sellable units of a variant off the shelf (a "Loss" adjustment per branch) and returns what is needed to put them back. */
export async function emptyVariantStock(token: string, productId: string, variantId: string): Promise<{ restore: () => Promise<void> }> {
  const rows = await api<{ items: StockRow[] }>("GET", `/api/v1/inventory/stock?productId=${productId}&PageSize=100`, undefined, token);
  const mine = rows.json.items.filter((r) => r.variantId === variantId && Number(r.available) > 0);
  const adjust = async (sign: 1 | -1, reason: string) => {
    for (const row of mine) {
      const response = await api("POST", "/api/v1/inventory/adjustments", { branchId: row.branchId, reason, note: "e2e", lines: [{ variantId, quantityChange: sign * Number(row.available) }] }, token);
      expect(response.status, JSON.stringify(response.json)).toBeLessThan(300);
    }
  };
  await adjust(-1, "Loss");
  return { restore: () => adjust(1, "Found") };
}

/** Changes a product's price by `delta` taka behind the API's back, to prove that a changed total is caught at checkout. */
export function bumpProductPrice(productId: string, delta: number) {
  // The products table has filtered unique indexes, so the update needs QUOTED_IDENTIFIER ON (sqlcmd starts with it off).
  sql(`SET QUOTED_IDENTIFIER ON; UPDATE Products SET RegularPrice = RegularPrice + (${delta}), SalePrice = CASE WHEN SalePrice IS NULL THEN NULL ELSE SalePrice + (${delta}) END WHERE Id = '${productId}'`);
}
