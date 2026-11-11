"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toast } from "@/components/ui/toaster";

/**
 * Typed access to the shop endpoints (Step 7-a: public catalog, cart, delivery, customer account). Every call goes through the BFF.
 * The guest cart token lives in an httpOnly cookie that the BFF adds to cart calls, so nothing here ever sees it.
 */
export type StoreProductCard = Schemas["StoreProductCard"];
export type StoreProductDetail = Schemas["StoreProductDetail"];
export type StoreCategory = Schemas["StoreCategory"];
export type StoreHome = Schemas["StoreHome"];
export type StoreFacets = Schemas["StoreFacets"];
export type StoreVariant = Schemas["StoreVariant"];
export type StoreImage = Schemas["StoreImage"];
export type CartView = Schemas["CartView"];
export type CartLine = Schemas["CartLineDto"];
export type DeliveryQuote = Schemas["DeliveryQuoteDto"];
export type Address = Schemas["AddressDto"];
export type SaveAddress = Schemas["SaveAddressRequest"];
export type WishlistItem = Schemas["WishlistItemDto"];
export type CustomerProfile = Schemas["CustomerProfileDto"];
export type CustomerSummary = Schemas["CustomerSummaryDto"];
export type Division = Schemas["DivisionDto"];
export type District = Schemas["DistrictDto"];
export type Upazila = Schemas["UpazilaDto"];
export type Country = Schemas["CountryDto"];
export type Consent = Schemas["ConsentDto"];

export type ProductQuery = {
  page?: number;
  pageSize?: number;
  category?: string;
  minPrice?: number;
  maxPrice?: number;
  size?: string;
  colour?: string;
  brand?: string;
  inStock?: boolean;
  onSale?: boolean;
  sort?: "newest" | "price-asc" | "price-desc" | "popular";
  q?: string;
};

const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

/* ---- Public catalog -------------------------------------------------------------------------------------------------------- */

export const useStoreHome = () => useQuery({ queryKey: ["store", "home"], staleTime: 60_000, queryFn: async () => unwrap(await api.GET("/api/v1/store/home")) });

export const useStoreCategories = () => useQuery({ queryKey: ["store", "categories"], staleTime: 5 * 60_000, queryFn: async () => unwrap(await api.GET("/api/v1/store/categories")) });

export function useStoreProducts(query: ProductQuery) {
  return useQuery({
    queryKey: ["store", "products", query],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/store/products", {
          params: { query: clean({ Page: query.page, PageSize: query.pageSize, category: query.category, minPrice: query.minPrice, maxPrice: query.maxPrice, size: query.size, colour: query.colour, brand: query.brand, inStock: query.inStock, onSale: query.onSale, sort: query.sort, q: query.q }) },
        }),
      ),
  });
}

export const useStoreFacets = (category?: string) =>
  useQuery({ queryKey: ["store", "facets", category ?? ""], staleTime: 60_000, queryFn: async () => unwrap(await api.GET("/api/v1/store/products/facets", { params: { query: clean({ category }) } })) });

export const useStoreProduct = (slug: string) =>
  useQuery({ queryKey: ["store", "product", slug], enabled: !!slug, queryFn: async () => unwrap(await api.GET("/api/v1/store/products/{slug}", { params: { path: { slug } } })) });

export function useSuggestions(q: string) {
  return useQuery({
    queryKey: ["store", "suggest", q],
    enabled: q.trim().length >= 2,
    staleTime: 30_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/store/search/suggest", { params: { query: { q: q.trim() } } })),
  });
}

/* ---- Geography and delivery ------------------------------------------------------------------------------------------------ */

export const useDivisions = () => useQuery({ queryKey: ["store", "geo", "divisions"], staleTime: Infinity, queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/divisions")) });

export const useDistricts = (divisionId?: string) =>
  useQuery({
    queryKey: ["store", "geo", "districts", divisionId ?? ""],
    enabled: !!divisionId,
    staleTime: Infinity,
    queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/districts", { params: { query: { divisionId: divisionId! } } })),
  });

export const useUpazilas = (districtId?: string) =>
  useQuery({
    queryKey: ["store", "geo", "upazilas", districtId ?? ""],
    enabled: !!districtId,
    staleTime: Infinity,
    queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/districts/{districtId}/upazilas", { params: { path: { districtId: districtId! } } })),
  });

export const useCountries = () => useQuery({ queryKey: ["store", "geo", "countries"], staleTime: 5 * 60_000, queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/countries")) });

/* ---- Cart ------------------------------------------------------------------------------------------------------------------ */

export const useCart = () => useQuery({ queryKey: ["store", "cart"], queryFn: async () => unwrap(await api.GET("/api/v1/store/cart")) });

/** Number of units in the cart, for the header badge (0 while loading or on error). */
export function useCartCount(): number {
  const cart = useCart();
  return (cart.data?.lines ?? []).reduce((sum, line) => sum + Number(line.quantity), 0);
}

function useStoreMutation<TVars, TData>(run: (vars: TVars) => Promise<TData>, keys: string[][] = [["store", "cart"], ["store", "me", "summary"]]) {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: run, onSuccess: () => Promise.all(keys.map((queryKey) => queryClient.invalidateQueries({ queryKey }))) });
}

export const useAddToCart = () => useStoreMutation(async (body: { variantId: string; quantity: number }) => unwrap(await api.POST("/api/v1/store/cart/items", { body })));
export const useSetCartQuantity = () =>
  useStoreMutation(async (vars: { variantId: string; quantity: number }) => unwrap(await api.PUT("/api/v1/store/cart/items/{variantId}", { params: { path: { variantId: vars.variantId } }, body: { quantity: vars.quantity } })));
export const useRemoveCartLine = () => useStoreMutation(async (variantId: string) => unwrap(await api.DELETE("/api/v1/store/cart/items/{variantId}", { params: { path: { variantId } } })));
export const useClearCart = () => useStoreMutation(async () => unwrap(await api.DELETE("/api/v1/store/cart")));
export const useApplyCoupon = () => useStoreMutation(async (code: string) => unwrap(await api.POST("/api/v1/store/cart/coupon", { body: { code } })));
export const useRemoveCoupon = () => useStoreMutation(async () => unwrap(await api.DELETE("/api/v1/store/cart/coupon")));
export const useCartDeliveryQuote = () =>
  useMutation({ mutationFn: async (body: Schemas["CartDeliveryRequest"]) => unwrap(await api.POST("/api/v1/store/cart/delivery-quote", { body })) });

/* ---- Customer account (needs a customer sign-in) -------------------------------------------------------------------------- */

export const useMe = (enabled = true) => useQuery({ queryKey: ["store", "me"], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/me")) });
export const useMeSummary = (enabled = true) => useQuery({ queryKey: ["store", "me", "summary"], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/me/summary")) });
export const useUpdateProfile = () =>
  useStoreMutation(async (body: Schemas["UpdateProfileRequest"]) => unwrap(await api.PUT("/api/v1/me", { body })), [["store", "me"]]);

export const useConsents = () => useQuery({ queryKey: ["store", "me", "consent"], queryFn: async () => unwrap(await api.GET("/api/v1/me/consent")) });
export const useSetConsent = () =>
  useStoreMutation(async (body: Schemas["SetConsentRequest"]) => unwrap(await api.PUT("/api/v1/me/consent", { body })), [["store", "me", "consent"]]);

export const useAddresses = () => useQuery({ queryKey: ["store", "me", "addresses"], queryFn: async () => unwrap(await api.GET("/api/v1/me/addresses")) });
const addressKeys = [["store", "me", "addresses"], ["store", "me", "summary"]];
export const useCreateAddress = () => useStoreMutation(async (body: SaveAddress) => unwrap(await api.POST("/api/v1/me/addresses", { body })), addressKeys);
export const useUpdateAddress = () =>
  useStoreMutation(async (vars: { id: string; body: SaveAddress }) => unwrap(await api.PUT("/api/v1/me/addresses/{id}", { params: { path: { id: vars.id } }, body: vars.body })), addressKeys);
export const useDeleteAddress = () => useStoreMutation(async (id: string) => unwrap(await api.DELETE("/api/v1/me/addresses/{id}", { params: { path: { id } } })), addressKeys);
export const useSetDefaultAddress = () => useStoreMutation(async (id: string) => unwrap(await api.POST("/api/v1/me/addresses/{id}/default", { params: { path: { id } } })), addressKeys);

export const useWishlist = (enabled = true) => useQuery({ queryKey: ["store", "me", "wishlist"], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/me/wishlist")) });
const wishlistKeys = [["store", "me", "wishlist"], ["store", "me", "summary"]];
export const useAddToWishlist = () => useStoreMutation(async (productId: string) => unwrap(await api.POST("/api/v1/me/wishlist", { body: { productId } })), wishlistKeys);
export const useRemoveFromWishlist = () => useStoreMutation(async (productId: string) => unwrap(await api.DELETE("/api/v1/me/wishlist/{productId}", { params: { path: { productId } } })), wishlistKeys);
export const useMoveToCart = () =>
  useStoreMutation(
    async (vars: { productId: string; variantId: string; quantity: number }) =>
      unwrap(await api.POST("/api/v1/me/wishlist/{productId}/move-to-cart", { params: { path: { productId: vars.productId } }, body: { variantId: vars.variantId, quantity: vars.quantity } })),
    [...wishlistKeys, ["store", "cart"]],
  );

/* ---- Errors ---------------------------------------------------------------------------------------------------------------- */

/** Shows an API error as a toast with friendly translated text; returns the field errors for forms. */
export function useReportError() {
  const message = useProblemMessage();
  return (error: unknown): Record<string, string> => {
    const problem = error instanceof ApiError ? error.problem : null;
    toast.error(message(problem));
    return problem ? fieldErrors(problem) : {};
  };
}
