import { createRequire } from "node:module";
import { describe, expect, it } from "vitest";

/** The service worker is a plain public file; it exports its rules for tests when loaded as CommonJS (no `self` here). */
const sw = createRequire(import.meta.url)("../public/sw.js") as {
  VERSION: string;
  NEVER_PREFIXES: string[];
  MEDIA_MAX_ENTRIES: number;
  MEDIA_MAX_AGE_MS: number;
  cacheName: (kind: string, version?: string) => string;
  currentCacheNames: (version?: string) => string[];
  isStaleCache: (name: string, version?: string) => boolean;
  isNeverPath: (p: string) => boolean;
  classify: (r: unknown, origin: string) => string;
  shouldCache: (r: unknown, res: unknown, origin?: string) => boolean;
  isExpired: (res: unknown, maxAge: number, now: number) => boolean;
  offlineAssetUrls: (html: string) => string[];
};

const ORIGIN = "https://shop.test";
const req = (path: string, init: { method?: string; mode?: string; headers?: Record<string, string> } = {}) => ({
  url: ORIGIN + path,
  method: init.method ?? "GET",
  mode: init.mode ?? "no-cors",
  headers: new Headers(init.headers),
});
const res = (headers: Record<string, string> = {}, status = 200, type = "basic") => ({ status, type, headers: new Headers(headers) });
const PUBLIC = { "cache-control": "public, max-age=31536000, immutable" };

describe("classify", () => {
  it("handles shop navigations, static files and product pictures", () => {
    expect(sw.classify(req("/shop", { mode: "navigate" }), ORIGIN)).toBe("navigate");
    expect(sw.classify(req("/", { mode: "navigate" }), ORIGIN)).toBe("navigate");
    expect(sw.classify(req("/_next/static/chunks/a.js"), ORIGIN)).toBe("static");
    expect(sw.classify(req("/brand/icon-192.png"), ORIGIN)).toBe("static");
    expect(sw.classify(req("/media/products/a.webp"), ORIGIN)).toBe("media");
  });

  it("ignores private and transactional paths, including navigations", () => {
    for (const p of ["/api/bff/auth/me", "/api", "/admin", "/admin/orders", "/account/orders", "/checkout", "/cart", "/login", "/pay/123", "/order/JV-1", "/track", "/track/x", "/first-login", "/forgot-password", "/reset-password"]) {
      expect(sw.classify(req(p), ORIGIN)).toBe("ignore");
      expect(sw.classify(req(p, { mode: "navigate" }), ORIGIN)).toBe("ignore");
    }
  });

  it("matches whole segments, so /orders-guide is not /order and /cartoon is not /cart", () => {
    expect(sw.isNeverPath("/order")).toBe(true);
    expect(sw.isNeverPath("/order/1")).toBe(true);
    expect(sw.isNeverPath("/orders-guide")).toBe(false);
    expect(sw.isNeverPath("/cartoon")).toBe(false);
  });

  it("ignores non-GET, other origins, authorised requests and the offline page itself", () => {
    expect(sw.classify(req("/media/a.webp", { method: "POST" }), ORIGIN)).toBe("ignore");
    expect(sw.classify({ ...req("/media/a.webp"), url: "https://cdn.example/media/a.webp" }, ORIGIN)).toBe("ignore");
    expect(sw.classify(req("/media/a.webp", { headers: { authorization: "Bearer x" } }), ORIGIN)).toBe("ignore");
    expect(sw.classify(req("/offline", { mode: "navigate" }), ORIGIN)).toBe("ignore");
    expect(sw.classify(req("/something-else"), ORIGIN)).toBe("ignore");
  });
});

describe("shouldCache", () => {
  it("accepts a public static asset and a product picture", () => {
    expect(sw.shouldCache(req("/_next/static/a.js"), res(PUBLIC), ORIGIN)).toBe(true);
    expect(sw.shouldCache(req("/media/p/a.webp"), res({ "cache-control": "public, max-age=31536000, immutable" }), ORIGIN)).toBe(true);
    expect(sw.shouldCache(req("/brand/icon-192.png"), res({}), ORIGIN)).toBe(true);
  });

  it("refuses responses with Set-Cookie, Authorization, no-store or private", () => {
    const r = req("/_next/static/a.js");
    expect(sw.shouldCache(r, res({ ...PUBLIC, "set-cookie": "a=b" }), ORIGIN)).toBe(false);
    expect(sw.shouldCache(r, res({ ...PUBLIC, authorization: "x" }), ORIGIN)).toBe(false);
    expect(sw.shouldCache(r, res({ "cache-control": "no-store" }), ORIGIN)).toBe(false);
    expect(sw.shouldCache(r, res({ "cache-control": "private, max-age=60" }), ORIGIN)).toBe(false);
    expect(sw.shouldCache(r, res({ "cache-control": "public, NO-STORE" }), ORIGIN)).toBe(false);
    expect(sw.shouldCache(r, res({ ...PUBLIC, vary: "*" }), ORIGIN)).toBe(false);
  });

  it("refuses failed, partial and opaque responses", () => {
    const r = req("/media/a.webp");
    expect(sw.shouldCache(r, res(PUBLIC, 404), ORIGIN)).toBe(false);
    expect(sw.shouldCache(r, res(PUBLIC, 206), ORIGIN)).toBe(false);
    expect(sw.shouldCache(r, res(PUBLIC, 200, "opaque"), ORIGIN)).toBe(false);
  });

  it("never caches private paths even when the response looks harmless", () => {
    for (const p of ["/api/bff/cart", "/admin", "/account", "/checkout", "/cart", "/login", "/pay/1", "/order/1", "/track"]) {
      expect(sw.shouldCache(req(p), res(PUBLIC), ORIGIN)).toBe(false);
      expect(sw.shouldCache(req(p, { mode: "navigate" }), res(PUBLIC), ORIGIN)).toBe(false);
    }
  });

  it("never caches non-GET requests or requests with Authorization", () => {
    expect(sw.shouldCache(req("/media/a.webp", { method: "POST" }), res(PUBLIC), ORIGIN)).toBe(false);
    expect(sw.shouldCache(req("/media/a.webp", { headers: { authorization: "Bearer x" } }), res(PUBLIC), ORIGIN)).toBe(false);
  });

  it("refuses a page the shop sends as no-store (every dynamic page)", () => {
    expect(sw.shouldCache(req("/shop", { mode: "navigate" }), res({ "cache-control": "private, no-cache, no-store, max-age=0, must-revalidate" }), ORIGIN)).toBe(false);
  });
});

describe("cache names and expiry", () => {
  it("builds versioned names and recognises old ones", () => {
    expect(sw.cacheName("static", "v7")).toBe("juvo-static-v7");
    expect(sw.currentCacheNames("v7")).toEqual(["juvo-static-v7", "juvo-media-v7", "juvo-offline-v7"]);
    expect(sw.isStaleCache("juvo-static-v1", "v2")).toBe(true);
    expect(sw.isStaleCache("juvo-media-v2", "v2")).toBe(false);
    expect(sw.isStaleCache("someone-elses-cache", "v2")).toBe(false);
    expect(sw.cacheName("media")).toBe(`juvo-media-${sw.VERSION}`);
  });

  it("expires media entries by age and treats a missing stamp as expired", () => {
    const now = 1_000_000_000_000;
    expect(sw.isExpired({ headers: new Headers({ "x-sw-cached-at": String(now - 1000) }) }, sw.MEDIA_MAX_AGE_MS, now)).toBe(false);
    expect(sw.isExpired({ headers: new Headers({ "x-sw-cached-at": String(now - sw.MEDIA_MAX_AGE_MS - 1) }) }, sw.MEDIA_MAX_AGE_MS, now)).toBe(true);
    expect(sw.isExpired({ headers: new Headers() }, sw.MEDIA_MAX_AGE_MS, now)).toBe(true);
    expect(sw.MEDIA_MAX_ENTRIES).toBeGreaterThan(0);
  });

  it("finds the scripts and styles an offline page needs", () => {
    const html = '<link href="/_next/static/css/a1.css"/><script src="/_next/static/chunks/b2.js"></script><img src="/brand/logo-badge-256.png"/><a href="/shop">';
    expect(sw.offlineAssetUrls(html).sort()).toEqual(["/_next/static/chunks/b2.js", "/_next/static/css/a1.css", "/brand/logo-badge-256.png"]);
  });
});
