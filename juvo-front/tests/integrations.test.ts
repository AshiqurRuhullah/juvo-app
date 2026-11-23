import { describe, expect, it } from "vitest";
import {
  API_SCOPES, WEBHOOK_EVENTS, anyDeliveryFilter, canRedeliver, checkWebhookUrl, curlExample, deliveryQuery, dhakaToday, emptyKeyForm, emptyWebhookForm, eventKey, expiryToIso, hasErrors,
  isoToDhakaDay, keyState, keyToForm, keyToRequest, needsPiiWarning, orderScopes, parseRate, scopeKey, summariseRedelivery, toggleScope, validateKey, validateWebhook, verifySample,
  webhookToRequest, type KeyForm, type WebhookForm,
} from "@/modules/integrations/logic";

const NOW = Date.parse("2026-12-01T10:00:00+06:00");
const key = (patch: Partial<KeyForm> = {}): KeyForm => ({ ...emptyKeyForm(), name: "Accounting sync", scopes: ["orders:read"], ...patch });
const hook = (patch: Partial<WebhookForm> = {}): WebhookForm => ({ ...emptyWebhookForm(), name: "ERP", url: "https://erp.example.com/hook", events: ["order.placed"], ...patch });

describe("scopes", () => {
  it("lists the five read-only scopes, personal data last", () => {
    expect(API_SCOPES).toEqual(["orders:read", "products:read", "stock:read", "customers:read", "pii:read"]);
  });

  it("orders the API's list in our order and keeps unknown scopes at the end", () => {
    const fromApi = [{ scope: "pii:read" }, { scope: "future:read" }, { scope: "orders:read" }, { scope: "stock:read" }];
    expect(orderScopes(fromApi).map((s) => s.scope)).toEqual(["orders:read", "stock:read", "pii:read", "future:read"]);
  });

  it("warns only when pii:read is chosen", () => {
    expect(needsPiiWarning(["orders:read"])).toBe(false);
    expect(needsPiiWarning(["orders:read", "pii:read"])).toBe(true);
  });

  it("toggles a scope on and off without duplicates", () => {
    expect(toggleScope(["orders:read"], "stock:read", true)).toEqual(["orders:read", "stock:read"]);
    expect(toggleScope(["orders:read", "stock:read"], "orders:read", false)).toEqual(["stock:read"]);
    expect(toggleScope(["orders:read"], "orders:read", true)).toEqual(["orders:read"]);
  });
});

describe("expiry in Dhaka", () => {
  it("is the end of the chosen Dhaka day", () => {
    expect(expiryToIso("2026-12-31")).toBe("2026-12-31T23:59:59+06:00");
    expect(new Date(expiryToIso("2026-12-31")!).toISOString()).toBe("2026-12-31T17:59:59.000Z");
  });

  it("refuses text that is not a real day", () => {
    expect(expiryToIso("")).toBeNull();
    expect(expiryToIso("31/12/2026")).toBeNull();
    expect(expiryToIso("2026-02-31")).toBeNull();
    expect(expiryToIso("2026-13-01")).toBeNull();
  });

  it("turns an instant back into its Dhaka day (late evening UTC is already tomorrow in Dhaka)", () => {
    expect(isoToDhakaDay("2026-12-31T17:59:59Z")).toBe("2026-12-31");
    expect(isoToDhakaDay("2026-12-31T18:00:00Z")).toBe("2027-01-01");
    expect(isoToDhakaDay(null)).toBe("");
  });

  it("round-trips through the form", () => {
    const form = keyToForm({ name: "x", scopes: ["orders:read"], expiresAt: "2026-12-31T17:59:59+00:00" });
    expect(form.expiresOn).toBe("2026-12-31");
    expect(keyToRequest(form).expiresAt).toBe("2026-12-31T23:59:59+06:00");
  });

  it("knows today in Dhaka", () => {
    expect(dhakaToday(Date.parse("2026-12-01T20:00:00Z"))).toBe("2026-12-02");
  });
});

describe("rate limit", () => {
  it("accepts whole numbers from 1 to 10000", () => {
    expect(parseRate("1")).toBe(1);
    expect(parseRate(" 10000 ")).toBe(10_000);
    expect(parseRate("0")).toBeNull();
    expect(parseRate("10001")).toBeNull();
    expect(parseRate("1.5")).toBeNull();
    expect(parseRate("-3")).toBeNull();
    expect(parseRate("abc")).toBeNull();
  });
});

describe("key validation", () => {
  it("accepts a name, one scope, no expiry and the default limit", () => {
    expect(validateKey(key(), API_SCOPES, NOW)).toEqual({});
  });

  it("wants a name of 2 to 80 characters", () => {
    expect(validateKey(key({ name: " a " }), API_SCOPES, NOW).name).toBe("nameLength");
    expect(validateKey(key({ name: "x".repeat(81) }), API_SCOPES, NOW).name).toBe("nameLength");
    expect(validateKey(key({ name: "ab" }), API_SCOPES, NOW).name).toBeUndefined();
  });

  it("wants at least one known scope", () => {
    expect(validateKey(key({ scopes: [] }), API_SCOPES, NOW).scopes).toBe("noScope");
    expect(validateKey(key({ scopes: ["orders:write"] }), API_SCOPES, NOW).scopes).toBe("unknownScope");
  });

  it("wants an expiry in the future and a valid limit", () => {
    expect(validateKey(key({ expiresOn: "2026-11-30" }), API_SCOPES, NOW).expiresOn).toBe("expiryPast");
    expect(validateKey(key({ expiresOn: "2026-12-01" }), API_SCOPES, NOW).expiresOn).toBeUndefined(); // the day ends at 23:59:59
    expect(validateKey(key({ expiresOn: "2026-02-30" }), API_SCOPES, NOW).expiresOn).toBe("expiryInvalid");
    expect(validateKey(key({ rateLimit: "20000" }), API_SCOPES, NOW).rateLimit).toBe("rateInvalid");
    expect(validateKey(key({ rateLimit: "120" }), API_SCOPES, NOW)).toEqual({});
  });

  it("builds the request with trimmed name, ordered scopes and nulls for empty options", () => {
    const request = keyToRequest(key({ name: "  Sync  ", scopes: ["stock:read", "orders:read"] }));
    expect(request).toEqual({ name: "Sync", scopes: ["orders:read", "stock:read"], branchId: null, expiresAt: null, rateLimitPerMinute: null });
    expect(keyToRequest(key({ branchId: "b1", rateLimit: "30" }))).toMatchObject({ branchId: "b1", rateLimitPerMinute: 30 });
  });

  it("reads a stored key back into the form", () => {
    expect(keyToForm({ name: "A", scopes: ["pii:read"], branchId: null, expiresAt: null, rateLimitPerMinute: 90 })).toEqual({ name: "A", scopes: ["pii:read"], branchId: "", expiresOn: "", rateLimit: "90" });
  });

  it("shows expired before disabled before enabled", () => {
    expect(keyState({ isEnabled: true, isExpired: true })).toBe("Expired");
    expect(keyState({ isEnabled: false, isExpired: false })).toBe("Disabled");
    expect(keyState({ isEnabled: true, isExpired: false })).toBe("Enabled");
    expect(hasErrors({})).toBe(false);
  });
});

describe("webhook address check (mirrors the API)", () => {
  it("accepts a plain https address", () => {
    expect(checkWebhookUrl("https://erp.example.com/hook")).toBe("ok");
    expect(checkWebhookUrl("  https://erp.example.com:8443/a?b=1  ")).toBe("ok");
  });

  it("refuses an empty, over-long or unparsable address", () => {
    expect(checkWebhookUrl("   ")).toBe("empty");
    expect(checkWebhookUrl("https://example.com/" + "a".repeat(500))).toBe("tooLong");
    expect(checkWebhookUrl("not a url")).toBe("invalid");
    expect(checkWebhookUrl("example.com/hook")).toBe("invalid");
  });

  it("refuses everything but https", () => {
    expect(checkWebhookUrl("http://erp.example.com/hook")).toBe("notHttps");
    expect(checkWebhookUrl("ftp://erp.example.com/hook")).toBe("notHttps");
    expect(checkWebhookUrl("javascript:alert(1)")).toBe("notHttps");
  });

  it("refuses a user name or password in the address", () => {
    expect(checkWebhookUrl("https://user@erp.example.com/hook")).toBe("credentials");
    expect(checkWebhookUrl("https://user:pw@erp.example.com/hook")).toBe("credentials");
  });

  it("leaves the public-address decision to the API (a loopback host passes the client check)", () => {
    expect(checkWebhookUrl("https://localhost/hook")).toBe("ok");
    expect(checkWebhookUrl("https://127.0.0.1/hook")).toBe("ok");
  });
});

describe("webhook form", () => {
  it("lists the five subscribable events (no ping)", () => {
    expect(WEBHOOK_EVENTS).toEqual(["order.placed", "order.status_changed", "product.published", "product.updated", "stock.low"]);
  });

  it("validates name, address and events", () => {
    expect(validateWebhook(hook())).toEqual({});
    expect(validateWebhook(hook({ name: "x" })).name).toBe("nameLength");
    expect(validateWebhook(hook({ url: "http://a.example.com" })).url).toBe("notHttps");
    expect(validateWebhook(hook({ events: [] })).events).toBe("noEvent");
    expect(validateWebhook(hook({ events: ["ping"] })).events).toBe("unknownEvent");
  });

  it("builds the request in the catalogue's event order", () => {
    expect(webhookToRequest(hook({ name: " ERP ", url: " https://a.example.com ", events: ["stock.low", "order.placed"], isEnabled: false }))).toEqual({
      name: "ERP", url: "https://a.example.com", events: ["order.placed", "stock.low"], isEnabled: false,
    });
  });
});

describe("message keys", () => {
  it("never puts a colon or a dot in a key path", () => {
    expect(scopeKey("orders:read")).toBe("orders_read");
    expect(eventKey("order.status_changed")).toBe("order_status_changed");
    for (const e of WEBHOOK_EVENTS) expect(eventKey(e)).not.toContain(".");
    for (const sc of API_SCOPES) expect(scopeKey(sc)).not.toContain(":");
  });
});

describe("delivery log", () => {
  it("builds the query and leaves empty filters out", () => {
    expect(deliveryQuery({ page: 2, pageSize: 10 }, {})).toEqual({ Page: 2, PageSize: 10 });
    expect(deliveryQuery({ page: 1, pageSize: 10, search: "abc" }, { endpointId: "e1", status: "Failed", eventType: "order.placed" })).toEqual({
      Page: 1, PageSize: 10, Search: "abc", endpointId: "e1", status: "Failed", eventType: "order.placed",
    });
  });

  it("never sends an unknown status", () => {
    expect(deliveryQuery({ page: 1, pageSize: 10 }, { status: "Whatever" })).toEqual({ Page: 1, PageSize: 10 });
  });

  it("knows when a filter is active", () => {
    expect(anyDeliveryFilter({}, "")).toBe(false);
    expect(anyDeliveryFilter({ status: "Failed" }, "")).toBe(true);
    expect(anyDeliveryFilter({}, "x")).toBe(true);
  });

  it("allows redelivery for failed and succeeded rows, not for pending ones", () => {
    expect(canRedeliver("Failed")).toBe(true);
    expect(canRedeliver("Succeeded")).toBe(true);
    expect(canRedeliver("Pending")).toBe(false);
  });

  it("summarises a batch of redeliveries", () => {
    expect(summariseRedelivery([true, false, true])).toEqual({ sent: 2, refused: 1 });
    expect(summariseRedelivery([])).toEqual({ sent: 0, refused: 0 });
  });
});

describe("help samples", () => {
  it("use placeholders only, never a key or secret shape", () => {
    const text = curlExample() + verifySample();
    expect(text).toContain("YOUR_API_KEY");
    expect(text).toContain("YOUR_WEBHOOK_SECRET");
    expect(text).not.toMatch(/jv_live_[A-Za-z0-9]{8}_/);
    expect(text).not.toMatch(/whsec_[0-9a-f]{16}/);
  });

  it("send the key in a header, not in the address", () => {
    expect(curlExample()).toContain('-H "X-Api-Key: YOUR_API_KEY"');
    expect(curlExample()).not.toMatch(/\?.*key=/i);
  });

  it("verification checks the timestamp window and compares in constant time", () => {
    const code = verifySample();
    expect(code).toContain("300");
    expect(code).toContain("timingSafeEqual");
    expect(code).toContain("`${parts.t}.${rawBody}`");
  });
});
