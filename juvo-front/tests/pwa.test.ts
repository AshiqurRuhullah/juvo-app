import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { DAY_MS, DISMISS_DAYS, EMPTY_STATE, isIosSafari, isQuietPath, parseState, shouldShowInstall } from "@/lib/pwa/rules";
import { MANIFEST_BACKGROUND_COLOR, MANIFEST_THEME_COLOR } from "@/lib/pwa/colors";
import manifest from "@/app/manifest";

const NOW = 1_800_000_000_000;

describe("install prompt timing", () => {
  it("waits for the second visit or a meaningful action", () => {
    expect(shouldShowInstall({ ...EMPTY_STATE, visits: 1 }, NOW, false)).toBe(false);
    expect(shouldShowInstall({ ...EMPTY_STATE, visits: 2 }, NOW, false)).toBe(true);
    expect(shouldShowInstall({ ...EMPTY_STATE, visits: 1, action: true }, NOW, false)).toBe(true);
  });

  it("stays hidden for 30 days after a dismissal, then may return", () => {
    const base = { ...EMPTY_STATE, visits: 5 };
    expect(DISMISS_DAYS).toBe(30);
    expect(shouldShowInstall({ ...base, dismissedAt: NOW - 29 * DAY_MS }, NOW, false)).toBe(false);
    expect(shouldShowInstall({ ...base, dismissedAt: NOW - 30 * DAY_MS }, NOW, false)).toBe(true);
  });

  it("never shows when the app is already installed", () => {
    expect(shouldShowInstall({ ...EMPTY_STATE, visits: 9, action: true }, NOW, true)).toBe(false);
  });

  it("reads stored state defensively", () => {
    expect(parseState(null)).toEqual(EMPTY_STATE);
    expect(parseState("not json")).toEqual(EMPTY_STATE);
    expect(parseState('{"visits":3,"dismissedAt":5,"action":true}')).toEqual({ visits: 3, dismissedAt: 5, action: true });
    expect(parseState('{"visits":"x","dismissedAt":"y"}')).toEqual(EMPTY_STATE);
  });
});

describe("where the prompt stays quiet", () => {
  it("is quiet in checkout, cart, payment, order, tracking, sign-in and admin", () => {
    for (const p of ["/checkout", "/cart", "/pay/1", "/order/2", "/track", "/login", "/account/orders", "/admin/orders", "/offline"]) expect(isQuietPath(p)).toBe(true);
    for (const p of ["/", "/shop", "/product/shirt", "/category/dresses", "/orders-guide"]) expect(isQuietPath(p)).toBe(false);
  });
});

describe("iOS hint", () => {
  const safari = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";
  it("recognises iPhone Safari only", () => {
    expect(isIosSafari(safari)).toBe(true);
    expect(isIosSafari(safari.replace("Version/17.0", "CriOS/120.0"))).toBe(false);
    expect(isIosSafari("Mozilla/5.0 (Linux; Android 14; Pixel 7) Chrome/120.0 Mobile Safari/537.36")).toBe(false);
  });
});

describe("web app manifest", () => {
  const m = manifest();
  it("has the installability fields", () => {
    expect(m.name).toBe("Juvo BD");
    expect(m.short_name).toBe("Juvo");
    expect(m.start_url).toBe("/");
    expect(m.scope).toBe("/");
    expect(m.display).toBe("standalone");
    expect(m.lang).toBe("en");
    const sizes = (m.icons ?? []).map((i) => `${i.sizes}:${i.purpose ?? "any"}`);
    expect(sizes).toEqual(expect.arrayContaining(["192x192:any", "512x512:any", "512x512:maskable"]));
    expect((m.shortcuts ?? []).map((s) => s.url)).toEqual(["/shop", "/track", "/cart"]);
  });

  it("uses the brand tokens for its colours", () => {
    const tokens = fs.readFileSync(path.resolve(__dirname, "../src/styles/tokens.css"), "utf8");
    expect(tokens).toContain(`--brand-primary: ${MANIFEST_THEME_COLOR};`);
    expect(tokens).toContain(`--brand-accent: ${MANIFEST_BACKGROUND_COLOR};`);
    expect(m.theme_color).toBe(MANIFEST_THEME_COLOR);
    expect(m.background_color).toBe(MANIFEST_BACKGROUND_COLOR);
  });

  it("points at icon files that exist", () => {
    for (const i of m.icons ?? []) expect(fs.existsSync(path.resolve(__dirname, "../public", i.src.slice(1)))).toBe(true);
  });
});
