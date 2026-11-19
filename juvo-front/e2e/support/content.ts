import { call } from "./cod";
import { adminAccess } from "./returns";

/** Helpers of the site-content specs: put the shared content back to the seeded state through the real staff API, so specs do not depend on each other. */

export const PAGE_KEYS = ["about", "faq", "delivery-policy", "return-policy", "privacy-policy", "terms"] as const;
export const BADGE_KEYS = ["secure-payment", "delivery-coverage", "return-policy", "support-hours"] as const;

export async function resetPages() {
  const token = await adminAccess();
  for (const key of PAGE_KEYS) {
    await call("POST", `/api/v1/content/pages/${key}/unpublish`, { token });
    await call("PUT", `/api/v1/content/pages/${key}`, { token, body: {}, expect: 200 });
  }
}

export async function resetBrand() {
  const token = await adminAccess();
  await call("PUT", "/api/v1/content/brand", { token, body: { businessName: "Juvo BD", email: "juvobd@gmail.com" }, expect: 200 });
}

export async function resetSocial() {
  const token = await adminAccess();
  for (const platform of ["instagram", "tiktok", "youtube", "whatsapp"]) await call("DELETE", `/api/v1/content/social/${platform}`, { token });
  await call("PUT", "/api/v1/content/social/facebook", { token, body: { url: "https://facebook.com/juvobd", isEnabled: true, sortOrder: 0 }, expect: 200 });
}

export async function resetBadges() {
  const token = await adminAccess();
  for (const [index, key] of BADGE_KEYS.entries()) {
    await call("PUT", `/api/v1/content/badges/${key}`, { token, body: { textEn: null, textBn: null, isEnabled: false, sortOrder: index }, expect: 200 });
  }
}

export async function resetContent() {
  await resetPages();
  await resetBrand();
  await resetSocial();
  await resetBadges();
}

export type PageApi = { key: string; titleEn: string | null; bodyEn: string | null; isPublished: boolean };

export async function readPage(key: string): Promise<PageApi> {
  return (await call<PageApi>("GET", `/api/v1/content/pages/${key}`, { token: await adminAccess(), expect: 200 })).json;
}

export type BrandApi = { businessName: string; phone: string | null; whatsApp: string | null; email: string | null; addressEn: string | null; workingHoursEn: string | null; latitude: number | null; longitude: number | null };

export async function readBrand(): Promise<BrandApi> {
  return (await call<BrandApi>("GET", "/api/v1/content/brand", { token: await adminAccess(), expect: 200 })).json;
}

export type SocialApi = { platform: string; url: string; isEnabled: boolean };

export async function readSocial(): Promise<SocialApi[]> {
  return (await call<SocialApi[]>("GET", "/api/v1/content/social", { token: await adminAccess(), expect: 200 })).json;
}

/** Gives a page a title and text and publishes it, straight on the API. */
export async function publishPage(key: string, titleEn: string, bodyEn: string, extra: Record<string, string> = {}) {
  const token = await adminAccess();
  await call("PUT", `/api/v1/content/pages/${key}`, { token, body: { titleEn, bodyEn, ...extra }, expect: 200 });
  await call("POST", `/api/v1/content/pages/${key}/publish`, { token, expect: 200 });
}
