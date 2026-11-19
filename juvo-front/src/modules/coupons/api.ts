"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import type { SaveCouponBody } from "./logic";

/** Typed access to the staff coupon endpoints (docs/storefront-api.md, "Coupons"). Every call goes through the BFF. */
export type Coupon = Schemas["CouponDto"];

const KEY = ["coupons"] as const;
const PAGE = 100;
/** Coupons are read in pages of 100 up to this many pages (5,000 coupons); the screen says so when there are more. */
export const MAX_PAGES = 50;

/**
 * Every coupon. The API can only filter by on/off, but the screen filters by derived status (expired, not started, used up) next to the
 * shared pager, so it needs the whole list; a shop's coupon list is small.
 */
export function useCoupons() {
  return useQuery({
    queryKey: [...KEY, "all"],
    queryFn: async () => {
      const items: Coupon[] = [];
      let total = 0;
      for (let page = 1; page <= MAX_PAGES; page++) {
        const answer = unwrap(await api.GET("/api/v1/promotions/coupons", { params: { query: { Page: page, PageSize: PAGE } as never } }));
        items.push(...answer.items);
        total = Number(answer.total);
        if (items.length >= total || answer.items.length === 0) break;
      }
      return { items, total };
    },
  });
}

function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: KEY });
}

export function useSaveCoupon() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (args: { id?: string; body: SaveCouponBody }) =>
      args.id
        ? unwrap(await api.PUT("/api/v1/promotions/coupons/{id}", { params: { path: { id: args.id } }, body: args.body }))
        : unwrap(await api.POST("/api/v1/promotions/coupons", { body: args.body })),
    onSuccess: invalidate,
  });
}

export function useDeleteCoupon() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/promotions/coupons/{id}", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}
