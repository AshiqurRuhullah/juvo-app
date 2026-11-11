import "server-only";
import type { StoreCategory, StoreFacets } from "@/modules/store/api";
import { PAGE_SIZE, parseListQuery, type ListQuery } from "@/modules/store/query-params";
import type { ProductPage } from "@/modules/store/use-products-infinite";
import { storeGet } from "./store";

export type RawSearchParams = Record<string, string | string[] | undefined>;

/** The first page of a product grid and its facets, fetched on the server so the page arrives with cards in it. */
export async function loadListing(category: string | undefined, raw: RawSearchParams) {
  const query: ListQuery = parseListQuery(raw);
  const [page, facets] = await Promise.all([
    storeGet<ProductPage>("/products", { Page: 1, PageSize: PAGE_SIZE, category, ...query }),
    storeGet<StoreFacets>("/products/facets", { category }),
  ]);
  return { query, page, facets };
}

/** Finds a category by slug anywhere in the tree and returns the path of ancestors down to it (for breadcrumbs). */
export function findCategoryPath(tree: StoreCategory[], slug: string, trail: StoreCategory[] = []): StoreCategory[] | null {
  for (const node of tree) {
    const here = [...trail, node];
    if (node.slug === slug) return here;
    const deeper = findCategoryPath(node.children, slug, here);
    if (deeper) return deeper;
  }
  return null;
}
