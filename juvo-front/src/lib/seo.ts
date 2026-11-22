/**
 * Pure helpers for search-engine and social-card output (FR-R01..R03): the site address, absolute links, noindex rules, the list of
 * private paths for robots.txt, and the JSON-LD builders. Nothing here reads the API or the request; callers pass real values in and
 * leave out what the API does not give (no invented text, no placeholder ratings).
 */

export const DEFAULT_SITE_URL = "http://localhost:3000";
export const DEFAULT_SITE_NAME = "Juvo BD";

/** Public address of the site without a trailing slash. `SITE_URL` (runtime) wins over `NEXT_PUBLIC_SITE_URL`; the fallback is localhost only. */
export function siteUrl(env: Record<string, string | undefined> = process.env): string {
  const raw = (env.SITE_URL || env.NEXT_PUBLIC_SITE_URL || DEFAULT_SITE_URL).trim();
  return raw.replace(/\/+$/, "") || DEFAULT_SITE_URL;
}

/** Turns a path (or an API `/files/...` address, served here under `/media/...`) into an absolute address. Undefined for nothing. */
export function absoluteUrl(path: string | null | undefined, base: string = siteUrl()): string | undefined {
  const value = path?.trim();
  if (!value) return undefined;
  if (/^https?:\/\//i.test(value)) return value;
  const served = value.startsWith("/files/") ? `/media/${value.slice("/files/".length)}` : value;
  return `${base}${served.startsWith("/") ? "" : "/"}${served}`;
}

/** Routes that must never be crawled or indexed (robots.txt Disallow list). The page-level noindex is kept as a second guard. */
export const PRIVATE_PATHS = [
  "/admin",
  "/account",
  "/cart",
  "/checkout",
  "/api",
  "/login",
  "/first-login",
  "/forgot-password",
  "/reset-password",
  "/track",
  "/order/",
  "/pay/",
  "/search",
  "/unsubscribe",
  "/contact/order-help",
  "/design",
  "/forbidden",
  "/unauthorized",
] as const;

/** True when a path is public and may be listed in the sitemap. */
export function isIndexablePath(path: string): boolean {
  return !PRIVATE_PATHS.some((p) => {
    const prefix = p.endsWith("/") ? p : `${p}/`;
    return path === p || path.startsWith(prefix);
  });
}

export const NOINDEX = { index: false, follow: false } as const;

/** The shop name for titles: the business name from the public brand, else the neutral default. */
export function shopName(brandName: string | null | undefined): string {
  return brandName?.trim() || DEFAULT_SITE_NAME;
}

/** JSON for an inline `<script type="application/ld+json">`: `<` (and the line separators) are escaped so the text can never close the tag. */
export function jsonLdString(data: unknown): string {
  return JSON.stringify(data).replace(/</g, "\\u003c").replace(/\u2028/g, "\\u2028").replace(/\u2029/g, "\\u2029");
}

/** Drops keys whose value is undefined, null or an empty array/string, so the output never carries empty placeholders. */
function clean<T extends Record<string, unknown>>(obj: T): T {
  return Object.fromEntries(
    Object.entries(obj).filter(([, v]) => v !== undefined && v !== null && v !== "" && !(Array.isArray(v) && v.length === 0)),
  ) as T;
}

export type ProductLdInput = {
  name: string;
  url: string;
  description?: string | null;
  sku?: string | null;
  images: string[];
  brand?: string | null;
  price: number | string;
  currency?: string;
  /** Any variant can be bought now. */
  inStock: boolean;
  /** Any variant is a pre-order (used only when nothing is in stock). */
  preorder?: boolean;
  /** Real approved reviews only; leave undefined when the API has none. */
  rating?: { value: number; count: number } | null;
};

export function availabilityUrl(inStock: boolean, preorder = false): string {
  if (inStock) return "https://schema.org/InStock";
  return preorder ? "https://schema.org/PreOrder" : "https://schema.org/OutOfStock";
}

export function buildProductJsonLd(input: ProductLdInput) {
  return clean({
    "@context": "https://schema.org",
    "@type": "Product",
    name: input.name,
    description: input.description ?? undefined,
    sku: input.sku ?? undefined,
    image: input.images,
    brand: input.brand ? { "@type": "Brand", name: input.brand } : undefined,
    offers: {
      "@type": "Offer",
      url: input.url,
      priceCurrency: input.currency ?? "BDT",
      price: Number(input.price),
      availability: availabilityUrl(input.inStock, input.preorder),
    },
    aggregateRating:
      input.rating && input.rating.count > 0
        ? { "@type": "AggregateRating", ratingValue: input.rating.value, reviewCount: input.rating.count }
        : undefined,
  });
}

export function buildBreadcrumbJsonLd(items: { name: string; url: string }[]) {
  if (items.length === 0) return null;
  return {
    "@context": "https://schema.org",
    "@type": "BreadcrumbList",
    itemListElement: items.map((item, index) => ({ "@type": "ListItem", position: index + 1, name: item.name, item: item.url })),
  };
}

export function buildOrganizationJsonLd(input: { name: string; url: string; logo?: string | null; sameAs?: string[] }) {
  return clean({
    "@context": "https://schema.org",
    "@type": "Organization",
    name: input.name,
    url: input.url,
    logo: input.logo ?? undefined,
    sameAs: (input.sameAs ?? []).filter((u) => /^https?:\/\//i.test(u)),
  });
}
