/**
 * Pure rules of the site-content screens (docs/content-api.md): page keys and public addresses, limits, form shapes, the checks that
 * run before a save (the API checks again and has the last word), and the language fallback of the public texts.
 */

// ---- pages ---------------------------------------------------------------------------------------------------

export const PAGE_KEYS = ["about", "faq", "delivery-policy", "return-policy", "privacy-policy", "terms"] as const;
export type PageKey = (typeof PAGE_KEYS)[number];

export function isPageKey(value: string): value is PageKey {
  return (PAGE_KEYS as readonly string[]).includes(value);
}

/** Public address of a page. Only the privacy page has a shorter address than its key. */
export function publicPath(key: PageKey): string {
  return key === "privacy-policy" ? "/privacy" : `/${key}`;
}

/** The page key behind a public address segment (`privacy` is the privacy page), or null. */
export function keyFromPublicSegment(segment: string): PageKey | null {
  if (segment === "privacy") return "privacy-policy";
  return isPageKey(segment) && segment !== "privacy-policy" ? segment : null;
}

export const LIMITS = {
  title: 200,
  metaTitle: 200,
  metaDescription: 400,
  body: 30000,
  businessName: 120,
  address: 500,
  hours: 300,
  badgeText: 160,
  sortOrder: 1000,
} as const;

export type PageForm = {
  titleEn: string;
  titleBn: string;
  bodyEn: string;
  bodyBn: string;
  metaTitleEn: string;
  metaTitleBn: string;
  metaDescriptionEn: string;
  metaDescriptionBn: string;
};

export type PageLike = { [K in keyof PageForm]: string | null };

export const emptyPageForm = (): PageForm => ({ titleEn: "", titleBn: "", bodyEn: "", bodyBn: "", metaTitleEn: "", metaTitleBn: "", metaDescriptionEn: "", metaDescriptionBn: "" });

export function pageToForm(page: PageLike): PageForm {
  return {
    titleEn: page.titleEn ?? "", titleBn: page.titleBn ?? "", bodyEn: page.bodyEn ?? "", bodyBn: page.bodyBn ?? "",
    metaTitleEn: page.metaTitleEn ?? "", metaTitleBn: page.metaTitleBn ?? "", metaDescriptionEn: page.metaDescriptionEn ?? "", metaDescriptionBn: page.metaDescriptionBn ?? "",
  };
}

export type PageErrorCode = "tooLong";
export type PageErrors = Partial<Record<keyof PageForm, PageErrorCode>>;

const PAGE_LIMIT: Record<keyof PageForm, number> = {
  titleEn: LIMITS.title, titleBn: LIMITS.title, bodyEn: LIMITS.body, bodyBn: LIMITS.body,
  metaTitleEn: LIMITS.metaTitle, metaTitleBn: LIMITS.metaTitle, metaDescriptionEn: LIMITS.metaDescription, metaDescriptionBn: LIMITS.metaDescription,
};

export function validatePage(form: PageForm): PageErrors {
  const errors: PageErrors = {};
  for (const key of Object.keys(PAGE_LIMIT) as (keyof PageForm)[]) if (form[key].length > PAGE_LIMIT[key]) errors[key] = "tooLong";
  return errors;
}

export const pageLimit = (key: keyof PageForm): number => PAGE_LIMIT[key];

/** True when the form differs from what is saved. */
export const pageIsDirty = (form: PageForm, saved: PageForm): boolean => (Object.keys(form) as (keyof PageForm)[]).some((k) => form[k] !== saved[k]);

/** Publishing needs an English title and body (BR: page cannot be published empty). The API says the same with CONTENT.PAGE_INCOMPLETE. */
export const canPublishForm = (form: PageForm): boolean => form.titleEn.trim() !== "" && form.bodyEn.trim() !== "";

// ---- public texts ----------------------------------------------------------------------------------------------

export type Localized = { en: string | null; bn: string | null };

/** The text for a language; Bengali falls back to English when the Bengali side is empty. Null when both are empty. */
export function pickText(text: Localized | null | undefined, locale: string): string | null {
  if (!text) return null;
  const wanted = locale === "bn" ? text.bn : text.en;
  const value = wanted?.trim() ? wanted : text.en;
  return value?.trim() ? value : null;
}

// ---- brand ---------------------------------------------------------------------------------------------------

export type BrandForm = {
  businessName: string;
  addressEn: string;
  addressBn: string;
  phone: string;
  whatsApp: string;
  email: string;
  workingHoursEn: string;
  workingHoursBn: string;
  latitude: string;
  longitude: string;
};

export type BrandLike = {
  businessName: string;
  logoFileRef: string | null;
  addressEn: string | null;
  addressBn: string | null;
  phone: string | null;
  whatsApp: string | null;
  email: string | null;
  workingHoursEn: string | null;
  workingHoursBn: string | null;
  latitude: number | string | null;
  longitude: number | string | null;
  updatedAt?: string;
};

export const emptyBrandForm = (): BrandForm => ({ businessName: "", addressEn: "", addressBn: "", phone: "", whatsApp: "", email: "", workingHoursEn: "", workingHoursBn: "", latitude: "", longitude: "" });

export function brandToForm(brand: BrandLike): BrandForm {
  return {
    businessName: brand.businessName, addressEn: brand.addressEn ?? "", addressBn: brand.addressBn ?? "", phone: brand.phone ?? "", whatsApp: brand.whatsApp ?? "", email: brand.email ?? "",
    workingHoursEn: brand.workingHoursEn ?? "", workingHoursBn: brand.workingHoursBn ?? "",
    latitude: brand.latitude === null || brand.latitude === undefined ? "" : String(brand.latitude),
    longitude: brand.longitude === null || brand.longitude === undefined ? "" : String(brand.longitude),
  };
}

export type BrandErrorCode = "required" | "tooLong" | "phone" | "email" | "coordinate" | "pair";
export type BrandErrors = Partial<Record<keyof BrandForm, BrandErrorCode>>;

/** The API checks a shop phone loosely: digits, space and + - ( ), 6 to 20 characters. */
export const isPhoneText = (value: string): boolean => /^[0-9 +\-()]{6,20}$/.test(value) && /\d/.test(value);
const isEmailText = (value: string): boolean => /^[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+$/.test(value);

function parseCoordinate(value: string): number | null {
  const text = value.trim();
  if (!/^-?\d+(\.\d+)?$/.test(text)) return null;
  return Number(text);
}

export function validateBrand(form: BrandForm): BrandErrors {
  const errors: BrandErrors = {};
  const name = form.businessName.trim();
  if (!name) errors.businessName = "required";
  else if (name.length > LIMITS.businessName) errors.businessName = "tooLong";
  for (const key of ["addressEn", "addressBn"] as const) if (form[key].length > LIMITS.address) errors[key] = "tooLong";
  for (const key of ["workingHoursEn", "workingHoursBn"] as const) if (form[key].length > LIMITS.hours) errors[key] = "tooLong";
  if (form.phone.trim() && !isPhoneText(form.phone.trim())) errors.phone = "phone";
  if (form.email.trim() && !isEmailText(form.email.trim())) errors.email = "email";
  const lat = form.latitude.trim();
  const lng = form.longitude.trim();
  if ((lat === "") !== (lng === "")) errors[lat === "" ? "latitude" : "longitude"] = "pair";
  else if (lat !== "") {
    const a = parseCoordinate(lat);
    const b = parseCoordinate(lng);
    if (a === null || a < -90 || a > 90) errors.latitude = "coordinate";
    if (b === null || b < -180 || b > 180) errors.longitude = "coordinate";
  }
  return errors;
}

export const hasErrors = (errors: object): boolean => Object.keys(errors).length > 0;

export type BrandRequest = {
  businessName: string;
  logoFileRef: string | null;
  addressEn: string | null;
  addressBn: string | null;
  phone: string | null;
  whatsApp: string | null;
  email: string | null;
  workingHoursEn: string | null;
  workingHoursBn: string | null;
  latitude: number | null;
  longitude: number | null;
};

const blankToNull = (value: string): string | null => (value.trim() === "" ? null : value.trim());

/** The save body. The logo reference is sent back unchanged because this slice has no upload (the API would clear it otherwise). */
export function brandToRequest(form: BrandForm, logoFileRef: string | null): BrandRequest {
  const lat = form.latitude.trim();
  return {
    businessName: form.businessName.trim(), logoFileRef,
    addressEn: blankToNull(form.addressEn), addressBn: blankToNull(form.addressBn), phone: blankToNull(form.phone), whatsApp: blankToNull(form.whatsApp), email: blankToNull(form.email),
    workingHoursEn: blankToNull(form.workingHoursEn), workingHoursBn: blankToNull(form.workingHoursBn),
    latitude: lat === "" ? null : Number(lat), longitude: lat === "" ? null : Number(form.longitude.trim()),
  };
}

// ---- social links ----------------------------------------------------------------------------------------------

export const PLATFORMS = ["Facebook", "Instagram", "TikTok", "YouTube", "WhatsApp"] as const;
export type Platform = (typeof PLATFORMS)[number];

export type SocialErrorCode = "required" | "https" | "invalid";

/**
 * Mirrors the API rule: an absolute https address with a dotted host and no user info. For WhatsApp a mobile number is also accepted
 * (the API turns it into a wa.me link). Returns null when fine.
 */
export function validateSocialInput(platform: Platform, raw: string): SocialErrorCode | null {
  const value = raw.trim();
  if (!value) return "required";
  if (platform === "WhatsApp" && /^\+?[0-9 \-()]{10,20}$/.test(value)) return null;
  if (/^http:\/\//i.test(value)) return "https";
  if (!/^https:\/\//i.test(value)) return "invalid";
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return "invalid";
  }
  if (url.protocol !== "https:" || url.username || url.password || !url.hostname.includes(".")) return "invalid";
  return null;
}

export type SocialRow = { platform: Platform; url: string; isEnabled: boolean; sortOrder: number | string };

/** Platforms in display order: saved ones first by sort order, the rest in the fixed order. */
export function socialRows<T extends { platform: string; sortOrder: number | string }>(saved: readonly T[]): { platform: Platform; saved: T | null }[] {
  const byPlatform = new Map(saved.map((s) => [s.platform.toLowerCase(), s]));
  return PLATFORMS.map((platform, index) => ({ platform, saved: byPlatform.get(platform.toLowerCase()) ?? null, index }))
    .sort((a, b) => (a.saved ? Number(a.saved.sortOrder) : 1e6 + a.index) - (b.saved ? Number(b.saved.sortOrder) : 1e6 + b.index) || a.index - b.index)
    .map(({ platform, saved: s }) => ({ platform, saved: s }));
}

/** Sort order for a link saved for the first time: after the last saved one. */
export const nextSortOrder = (saved: readonly { sortOrder: number | string }[]): number =>
  Math.min(LIMITS.sortOrder, saved.length === 0 ? 0 : Math.max(...saved.map((s) => Number(s.sortOrder))) + 1);

// ---- badges --------------------------------------------------------------------------------------------------

export const BADGE_KEYS = ["secure-payment", "delivery-coverage", "return-policy", "support-hours"] as const;
export type BadgeKey = (typeof BADGE_KEYS)[number];

export type BadgeForm = { textEn: string; textBn: string; isEnabled: boolean };
export type BadgeErrorCode = "tooLong" | "needEnglish";
export type BadgeErrors = Partial<Record<"textEn" | "textBn", BadgeErrorCode>>;

export function validateBadge(form: BadgeForm): BadgeErrors {
  const errors: BadgeErrors = {};
  if (form.textEn.length > LIMITS.badgeText) errors.textEn = "tooLong";
  if (form.textBn.length > LIMITS.badgeText) errors.textBn = "tooLong";
  if (form.isEnabled && !form.textEn.trim() && !errors.textEn) errors.textEn = "needEnglish";
  return errors;
}

// ---- rights --------------------------------------------------------------------------------------------------

export type ContentRights = {
  pageView: boolean;
  pageEdit: boolean;
  pagePublish: boolean;
  brandView: boolean;
  brandEdit: boolean;
  socialEdit: boolean;
  badgeEdit: boolean;
};
