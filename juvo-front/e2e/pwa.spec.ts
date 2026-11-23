import { expect, test, type BrowserContext, type Page } from "@playwright/test";

/**
 * Installable web app (FR-AF07): manifest, service worker, offline page, never-cache rules, install prompt. The e2e web server is a
 * production build, so the worker is active. Real browser, real server, no mocks.
 */

const pngSize = (b: Buffer) => ({ w: b.readUInt32BE(16), h: b.readUInt32BE(20) });

async function swReady(page: Page) {
  await page.waitForFunction(() => "serviceWorker" in navigator);
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
}

/** Opens the shop, waits for the worker to be active and in control (a reload makes sure of it). */
async function controlled(page: Page, path = "/shop") {
  await page.goto(path);
  await swReady(page);
  await page.reload();
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);
}

const cacheEntries = (page: Page) =>
  page.evaluate(async () => {
    const out: Record<string, string[]> = {};
    for (const name of await caches.keys()) {
      const cache = await caches.open(name);
      out[name] = (await cache.keys()).map((r) => new URL(r.url).pathname + new URL(r.url).search);
    }
    return out;
  });

test.describe("manifest and worker files", () => {
  test("the manifest is valid and its icons exist with the right sizes", async ({ request }) => {
    const res = await request.get("/manifest.webmanifest");
    expect(res.status()).toBe(200);
    expect(res.headers()["content-type"]).toContain("manifest+json");
    const m = await res.json();
    expect(m).toMatchObject({ name: "Juvo BD", short_name: "Juvo", start_url: "/", scope: "/", display: "standalone", lang: "en", theme_color: "#DB0100", background_color: "#EBE5DB" });
    expect(m.shortcuts.map((s: { url: string }) => s.url)).toEqual(["/shop", "/track", "/cart"]);
    const purposes = m.icons.map((i: { sizes: string; purpose: string }) => `${i.sizes}:${i.purpose}`);
    expect(purposes).toEqual(expect.arrayContaining(["192x192:any", "512x512:any", "512x512:maskable"]));
    for (const icon of m.icons as { src: string; sizes: string; type: string }[]) {
      const r = await request.get(icon.src);
      expect(r.status(), icon.src).toBe(200);
      expect(r.headers()["content-type"]).toContain(icon.type);
      const { w, h } = pngSize(await r.body());
      expect(`${w}x${h}`).toBe(icon.sizes);
    }
  });

  test("sw.js is served as JavaScript, never cached for long, with the scope header", async ({ request }) => {
    const res = await request.get("/sw.js", { maxRedirects: 0 });
    expect(res.status()).toBe(200);
    expect(res.headers()["content-type"]).toContain("javascript");
    expect(res.headers()["cache-control"]).toContain("no-store");
    expect(res.headers()["service-worker-allowed"]).toBe("/");
    expect(await res.text()).toContain('addEventListener("fetch"');
  });

  test("the offline page is public, branded and bilingual", async ({ page, request }) => {
    const res = await request.get("/offline", { maxRedirects: 0 });
    expect(res.status()).toBe(200);
    await page.goto("/offline");
    await expect(page.getByRole("heading", { name: "You are offline" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "আপনি অফলাইনে আছেন" })).toBeVisible();
    await expect(page.getByRole("button", { name: /Try again/ })).toBeVisible();
    await expect(page.locator('meta[name="robots"]')).toHaveAttribute("content", /noindex/);
    const robots = await (await request.get("/robots.txt")).text();
    expect(robots).not.toMatch(/Disallow: \/(sw\.js|offline|manifest)/);
  });
});

test.describe("service worker on the shop", () => {
  let context: BrowserContext;
  let page: Page;

  test.beforeEach(async ({ browser }) => {
    context = await browser.newContext();
    page = await context.newPage();
  });
  test.afterEach(async () => {
    await context.close();
  });

  test("registers on a shop page and controls it after a reload; stores the offline page and static files", async () => {
    await controlled(page);
    const reg = await page.evaluate(async () => {
      const r = await navigator.serviceWorker.getRegistration();
      return { scope: r?.scope, active: !!r?.active };
    });
    expect(reg.active).toBe(true);
    expect(reg.scope).toMatch(/\/$/);
    const names = Object.keys(await cacheEntries(page));
    expect(names).toEqual(expect.arrayContaining(["juvo-offline-v1", "juvo-static-v1"]));
    await expect.poll(async () => (await cacheEntries(page))["juvo-offline-v1"]).toContain("/offline");
    await expect.poll(async () => ((await cacheEntries(page))["juvo-static-v1"] ?? []).some((u) => u.startsWith("/_next/static/"))).toBe(true);
  });

  test("offline: an uncached shop page shows the offline page", async () => {
    await controlled(page);
    await context.setOffline(true);
    await page.goto("/category/never-visited-page");
    await expect(page.getByRole("heading", { name: "You are offline" })).toBeVisible();
    await expect(page.getByRole("heading", { name: "আপনি অফলাইনে আছেন" })).toBeVisible();
    // the stored page still has its script: the retry button reloads, and with the network back the shop answers
    await context.setOffline(false);
    await page.goto("/shop");
    await expect(page.getByRole("heading", { name: "You are offline" })).toHaveCount(0);
  });

  test("offline: cart, checkout, account and admin pages are not served from any cache", async () => {
    await controlled(page);
    for (const p of ["/cart", "/checkout", "/track", "/login"]) await page.goto(p); // visited online, must not be stored
    await context.setOffline(true);
    for (const p of ["/cart", "/checkout", "/track", "/login", "/account", "/admin"]) {
      await expect(page.goto(p), p).rejects.toThrow(/ERR_INTERNET_DISCONNECTED|net::ERR/);
    }
    await context.setOffline(false);
  });

  test("nothing personal lands in a cache: no api, bff, cart, checkout, account or admin url", async () => {
    await controlled(page);
    await page.evaluate(async () => {
      await fetch("/api/bff/auth/me").catch(() => null);
      await fetch("/api/bff/store/cart").catch(() => null);
    });
    for (const p of ["/cart", "/checkout", "/track", "/login", "/shop"]) await page.goto(p);
    await page.waitForTimeout(500); // let background cache writes finish
    const all = Object.values(await cacheEntries(page)).flat();
    expect(all.length).toBeGreaterThan(0);
    for (const url of all) {
      expect(url, url).not.toMatch(/^\/(api|admin|account|checkout|cart|login|pay|order|track)(\/|$|\?)/);
      expect(url, url).toMatch(/^\/(_next\/static|brand|media|offline)/);
    }
  });
});

test.describe("install prompt", () => {
  async function firePrompt(page: Page) {
    await expect(async () => {
      await page.evaluate(() => window.dispatchEvent(new Event("beforeinstallprompt", { cancelable: true })));
      await expect(page.getByTestId("pwa-prompt")).toBeVisible({ timeout: 500 });
    }).toPass({ timeout: 10_000 });
  }

  test("appears from the second visit, stays hidden after a dismissal (and across later visits)", async ({ browser }) => {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto("/shop");
    await swReady(page);
    // first visit: nothing yet, even if the browser offers installation
    await page.evaluate(() => window.dispatchEvent(new Event("beforeinstallprompt", { cancelable: true })));
    await page.waitForTimeout(400);
    await expect(page.getByTestId("pwa-prompt")).toHaveCount(0);

    // second visit = a new browser session
    await page.evaluate(() => sessionStorage.removeItem("juvo_pwa_visit"));
    await page.reload();
    await firePrompt(page);
    const box = page.getByTestId("pwa-prompt");
    await expect(box.getByRole("button", { name: "Install", exact: true })).toBeVisible();
    for (const b of await box.getByRole("button").all()) expect((await b.boundingBox())!.height).toBeGreaterThanOrEqual(43.5);

    // never inside checkout
    await page.goto("/checkout");
    await page.evaluate(() => window.dispatchEvent(new Event("beforeinstallprompt", { cancelable: true })));
    await page.waitForTimeout(400);
    await expect(page.getByTestId("pwa-prompt")).toHaveCount(0);

    await page.goto("/shop");
    await firePrompt(page);
    await page.getByTestId("pwa-prompt").getByRole("button", { name: "Not now" }).click();
    await expect(page.getByTestId("pwa-prompt")).toHaveCount(0);
    const stored = JSON.parse((await page.evaluate(() => localStorage.getItem("juvo_pwa"))) ?? "{}");
    expect(typeof stored.dismissedAt).toBe("number");

    // a later visit still shows nothing
    await page.evaluate(() => sessionStorage.removeItem("juvo_pwa_visit"));
    await page.reload();
    await page.evaluate(() => window.dispatchEvent(new Event("beforeinstallprompt", { cancelable: true })));
    await page.waitForTimeout(500);
    await expect(page.getByTestId("pwa-prompt")).toHaveCount(0);
    await context.close();
  });
});

test.describe("the admin panel", () => {
  test("has no service worker registration and no install prompt", async ({ browser }) => {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto("/admin"); // redirects to the staff sign-in
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(500);
    const regs = await page.evaluate(async () => (await navigator.serviceWorker.getRegistrations()).length);
    expect(regs).toBe(0);
    await expect(page.getByTestId("pwa-prompt")).toHaveCount(0);
    await context.close();
  });
});
