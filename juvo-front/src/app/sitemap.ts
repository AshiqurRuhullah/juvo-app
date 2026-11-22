import type { MetadataRoute } from "next";
import { absoluteUrl, siteUrl } from "@/lib/seo";
import { storeGet } from "@/lib/server/store";
import { PAGE_KEYS, publicPath } from "@/modules/content/logic";
import { getPublicPage } from "@/modules/content/public";
import type { ProductPage } from "@/modules/store/use-products-infinite";
import type { StoreCategory } from "@/modules/store/api";

// Built on request (it reads the live catalogue); the API answers are cached for a minute by storeGet.
export const dynamic = "force-dynamic";

/** Paging limits: the API caps a page at 100 rows; the sitemap lists at most 5,000 products (a sitemap file may hold 50,000 URLs). */
const PAGE_SIZE = 100;
const MAX_PRODUCT_PAGES = 50;

function flatten(tree: StoreCategory[]): StoreCategory[] {
  return tree.flatMap((node) => [node, ...flatten(node.children)]);
}

/**
 * Public, indexable addresses only: the shop pages, published content pages (with their real update time), categories and published
 * products. The public API gives no update time for products and categories, so those have no `lastModified`.
 */
export default async function sitemap(): Promise<MetadataRoute.Sitemap> {
  const base = siteUrl();
  const url = (path: string) => absoluteUrl(path, base) as string;
  const entries: MetadataRoute.Sitemap = [{ url: url("/") }, { url: url("/shop") }, { url: url("/contact") }];

  const tree = (await storeGet<StoreCategory[]>("/categories", undefined, { fresh: true })) ?? [];
  for (const category of flatten(tree)) entries.push({ url: url(`/category/${encodeURIComponent(category.slug)}`) });

  for (const key of PAGE_KEYS) {
    const page = await getPublicPage(key); // null when unpublished: then it is not listed
    if (page) entries.push({ url: url(publicPath(key)), lastModified: new Date(page.updatedAt) });
  }

  const seen = new Set<string>();
  for (let pageNumber = 1; pageNumber <= MAX_PRODUCT_PAGES; pageNumber++) {
    const page = await storeGet<ProductPage>("/products", { Page: pageNumber, PageSize: PAGE_SIZE }, { fresh: true });
    if (!page || page.items.length === 0) break;
    for (const item of page.items) {
      if (seen.has(item.slug)) continue;
      seen.add(item.slug);
      entries.push({ url: url(`/product/${encodeURIComponent(item.slug)}`) });
    }
    if (pageNumber * PAGE_SIZE >= Number(page.total)) break;
  }
  return entries;
}
