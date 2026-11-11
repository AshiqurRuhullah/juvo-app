"use client";

import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import type { ProductQuery } from "./api";
import { PAGE_SIZE } from "./query-params";

export type ProductPage = Schemas["PagedResultOfStoreProductCard"];

/**
 * The product grid's data: page after page of cards for "Load more". `initial` is the first page the server already fetched, used
 * only when it belongs to exactly this query (so the first paint has the cards in it and there is no loading flash).
 */
export function useProductsInfinite(query: Omit<ProductQuery, "page" | "pageSize">, initial?: { query: Omit<ProductQuery, "page" | "pageSize">; page: ProductPage | null }) {
  const useInitial = !!initial?.page && JSON.stringify(initial.query) === JSON.stringify(query);
  return useInfiniteQuery({
    queryKey: ["store", "products", "infinite", query],
    initialPageParam: 1,
    placeholderData: keepPreviousData,
    initialData: useInitial ? { pages: [initial!.page as ProductPage], pageParams: [1] } : undefined,
    staleTime: useInitial ? 60_000 : 0,
    queryFn: async ({ pageParam }) =>
      unwrap(
        await api.GET("/api/v1/store/products", {
          params: {
            query: Object.fromEntries(
              Object.entries({ Page: pageParam, PageSize: PAGE_SIZE, ...query }).filter(([, v]) => v !== undefined && v !== "" && v !== false),
            ),
          },
        }),
      ),
    getNextPageParam: (last) => {
      const pages = Number(last.totalPages ?? Math.ceil(Number(last.total) / Number(last.pageSize || PAGE_SIZE)));
      return Number(last.page) < pages ? Number(last.page) + 1 : undefined;
    },
  });
}
