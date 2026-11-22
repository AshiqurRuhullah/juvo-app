import type { MetadataRoute } from "next";
import { PRIVATE_PATHS, siteUrl } from "@/lib/seo";

// Built on request so SITE_URL can be set when the server starts (no rebuild).
export const dynamic = "force-dynamic";

/** robots.txt: the shop is open, private areas are closed, the sitemap is announced. The address comes from SITE_URL / NEXT_PUBLIC_SITE_URL. */
export default function robots(): MetadataRoute.Robots {
  return {
    rules: [{ userAgent: "*", allow: "/", disallow: [...PRIVATE_PATHS] }],
    sitemap: `${siteUrl()}/sitemap.xml`,
  };
}
