import "server-only";
import { apiBaseUrl } from "@/lib/server/backend";
import type { Schemas } from "@/lib/api/types";
import { PAGE_KEYS, type PageKey } from "./logic";

/**
 * Server-side reads of the public content API (`/api/v1/store/content/...`, docs/content-api.md section 3). All answers are public.
 * A page body is always read fresh, because staff check it right after publishing. The small shop-wide pieces (footer links, brand,
 * social links, badges) are kept for CONTENT_REVALIDATE_SECONDS (default 60, the same as the API's own cache) so a storefront page does
 * not call the API ten times per view. A missing or unreachable answer is `null`: the caller hides the block (no fake text).
 */
export type PublicPage = Schemas["PublicPageDto"];
export type PublicBrand = Schemas["PublicBrandDto"];
export type PublicSocialLink = Schemas["PublicSocialLinkDto"];
export type PublicBadge = Schemas["PublicTrustBadgeDto"];

const SHARED_SECONDS = (() => {
  const raw = Number(process.env.CONTENT_REVALIDATE_SECONDS);
  return Number.isFinite(raw) && raw >= 0 ? raw : 60;
})();

async function read<T>(path: string, fresh: boolean): Promise<T | null> {
  try {
    const response = await fetch(`${apiBaseUrl()}/api/v1/store/content${path}`, {
      headers: { Accept: "application/json" },
      ...(fresh || SHARED_SECONDS === 0 ? { cache: "no-store" as const } : { next: { revalidate: SHARED_SECONDS } }),
    });
    if (!response.ok) return null;
    return (await response.json()) as T;
  } catch {
    return null;
  }
}

export const getPublicPage = (key: PageKey) => read<PublicPage>(`/pages/${key}`, true);
export const getPublicBrand = () => read<PublicBrand>("/brand", false);
export const getPublicSocial = () => read<PublicSocialLink[]>("/social", false);
export const getPublicBadges = () => read<PublicBadge[]>("/badges", false);

/** Keys of the pages that are published right now (an unpublished page answers 404). Used for the footer links. */
export async function getPublishedKeys(): Promise<PageKey[]> {
  const answers = await Promise.all(PAGE_KEYS.map(async (key) => ((await read<PublicPage>(`/pages/${key}`, false)) ? key : null)));
  return answers.filter((key): key is PageKey => key !== null);
}
