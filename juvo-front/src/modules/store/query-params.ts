import type { ProductQuery } from "./api";

/** How many cards one page of the product grid holds. */
export const PAGE_SIZE = 12;

export const SORTS = ["newest", "price-asc", "price-desc", "popular"] as const;
export type Sort = (typeof SORTS)[number];

/** The filters of a product grid: everything in the address bar except the page. */
export type ListQuery = Omit<ProductQuery, "page" | "pageSize" | "category">;

type Raw = Record<string, string | string[] | undefined> | URLSearchParams;

const read = (raw: Raw, key: string): string | undefined => {
  const value = raw instanceof URLSearchParams ? (raw.get(key) ?? undefined) : raw[key];
  return Array.isArray(value) ? value[0] : value;
};

const amount = (value: string | undefined): number | undefined => {
  if (!value) return undefined;
  const n = Number(value);
  return Number.isFinite(n) && n >= 0 && n < 10_000_000 ? n : undefined;
};

const text = (value: string | undefined, max = 80): string | undefined => {
  const t = value?.trim();
  return t ? t.slice(0, max) : undefined;
};

/** Address-bar parameters to filters. Anything unknown or malformed is ignored, so a hand-edited address never breaks the page. */
export function parseListQuery(raw: Raw): ListQuery {
  const sort = read(raw, "sort");
  return {
    q: text(read(raw, "q"), 100),
    sort: (SORTS as readonly string[]).includes(sort ?? "") && sort !== "newest" ? (sort as Sort) : undefined,
    minPrice: amount(read(raw, "minPrice")),
    maxPrice: amount(read(raw, "maxPrice")),
    size: text(read(raw, "size"), 30),
    colour: text(read(raw, "colour"), 60),
    brand: text(read(raw, "brand"), 80),
    inStock: read(raw, "inStock") === "1" ? true : undefined,
    onSale: read(raw, "onSale") === "1" ? true : undefined,
  };
}

/** Filters to address-bar parameters (empty ones are left out so addresses stay short and shareable). */
export function toSearchParams(query: ListQuery): URLSearchParams {
  const params = new URLSearchParams();
  if (query.q) params.set("q", query.q);
  if (query.sort && query.sort !== "newest") params.set("sort", query.sort);
  if (query.minPrice !== undefined) params.set("minPrice", String(query.minPrice));
  if (query.maxPrice !== undefined) params.set("maxPrice", String(query.maxPrice));
  if (query.size) params.set("size", query.size);
  if (query.colour) params.set("colour", query.colour);
  if (query.brand) params.set("brand", query.brand);
  if (query.inStock) params.set("inStock", "1");
  if (query.onSale) params.set("onSale", "1");
  return params;
}

/** The number of active filters (sorting and the search text are not counted: they have their own controls). */
export function activeFilterCount(query: ListQuery): number {
  return [query.minPrice !== undefined || query.maxPrice !== undefined, !!query.size, !!query.colour, !!query.brand, !!query.inStock, !!query.onSale].filter(Boolean).length;
}
