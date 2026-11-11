"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Check, SearchX, SlidersHorizontal, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogDescription, DialogTitle, DialogTrigger, SheetContent } from "@/components/ui/overlays";
import { Alert, EmptyState } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { useMoney } from "@/modules/catalog/format";
import { useStoreFacets, type ProductQuery, type StoreFacets } from "./api";
import { activeFilterCount, parseListQuery, toSearchParams, type ListQuery, type Sort } from "./query-params";
import { ProductGrid, ProductGridSkeleton } from "./store-product-card";
import { useProductsInfinite, type ProductPage } from "./use-products-infinite";

type Props = {
  /** Category slug whose products (and sub-categories') are listed; none for the whole shop or for a search. */
  category?: string;
  /** Free-text search shown in the address bar as ?q= (the search page). */
  searchPage?: boolean;
  initialQuery: ListQuery;
  initialPage: ProductPage | null;
  initialFacets?: StoreFacets | null;
};

const SORT_OPTIONS: Sort[] = ["newest", "popular", "price-asc", "price-desc"];

function FilterGroup({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <fieldset className="space-y-2 border-b border-line pb-4">
      <legend className="mb-2 text-sm font-semibold text-strong">{title}</legend>
      {children}
    </fieldset>
  );
}

function Toggle({ checked, onChange, children }: { checked: boolean; onChange: (value: boolean) => void; children: React.ReactNode }) {
  return (
    <label className="flex min-h-11 cursor-pointer items-center gap-3 text-sm text-strong">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} className="size-5 accent-[var(--color-primary)]" />
      {children}
    </label>
  );
}

/** All the filters, as controls that apply at once. Used in the desktop sidebar and in the phone's bottom sheet. */
function FilterPanel({ query, facets, onChange }: { query: ListQuery; facets: StoreFacets | undefined; onChange: (patch: Partial<ListQuery>) => void }) {
  const t = useTranslations("store.filters");
  const money = useMoney();
  const [min, setMin] = React.useState(query.minPrice?.toString() ?? "");
  const [max, setMax] = React.useState(query.maxPrice?.toString() ?? "");
  const price = (value: string) => (value.trim() === "" || !Number.isFinite(Number(value)) ? undefined : Math.max(0, Number(value)));

  return (
    <div className="space-y-4">
      <FilterGroup title={t("price")}>
        <div className="flex items-center gap-2">
          <label className="flex-1">
            <span className="sr-only">{t("minPrice")}</span>
            <input inputMode="numeric" placeholder={facets ? money(facets.minPrice) : t("min")} value={min} onChange={(e) => setMin(e.target.value)} className="h-11 w-full rounded-control border border-line bg-card px-3 text-sm" />
          </label>
          <span aria-hidden className="text-muted">–</span>
          <label className="flex-1">
            <span className="sr-only">{t("maxPrice")}</span>
            <input inputMode="numeric" placeholder={facets ? money(facets.maxPrice) : t("max")} value={max} onChange={(e) => setMax(e.target.value)} className="h-11 w-full rounded-control border border-line bg-card px-3 text-sm" />
          </label>
        </div>
        <Button type="button" variant="secondary" onClick={() => onChange({ minPrice: price(min), maxPrice: price(max) })}>
          {t("applyPrice")}
        </Button>
      </FilterGroup>

      {facets && facets.sizes.length > 0 ? (
        <FilterGroup title={t("size")}>
          <div className="flex flex-wrap gap-2">
            {facets.sizes.map((size) => (
              <button
                key={size}
                type="button"
                aria-pressed={query.size === size}
                onClick={() => onChange({ size: query.size === size ? undefined : size })}
                className={cn("min-h-11 min-w-11 rounded-control border px-3 text-sm font-medium", query.size === size ? "border-primary bg-primary-soft text-primary-dark" : "border-line text-strong hover:bg-sunken")}
              >
                {size}
              </button>
            ))}
          </div>
        </FilterGroup>
      ) : null}

      {facets && facets.colours.length > 0 ? (
        <FilterGroup title={t("colour")}>
          <div className="flex flex-wrap gap-2">
            {facets.colours.map((colour) => {
              const on = query.colour === colour.name;
              return (
                <button
                  key={colour.name}
                  type="button"
                  aria-pressed={on}
                  aria-label={colour.name}
                  title={colour.name}
                  onClick={() => onChange({ colour: on ? undefined : colour.name })}
                  className={cn("tap relative flex size-11 items-center justify-center rounded-full border-2", on ? "border-primary" : "border-transparent")}
                >
                  <span className="block size-7 rounded-full border border-line" style={{ backgroundColor: colour.hex ?? "var(--surface-sunken)" }} />
                  {on ? <Check className="absolute size-4 text-white mix-blend-difference" aria-hidden /> : null}
                </button>
              );
            })}
          </div>
        </FilterGroup>
      ) : null}

      {facets && facets.brands.length > 0 ? (
        <FilterGroup title={t("brand")}>
          {facets.brands.map((brand) => (
            <Toggle key={brand.slug} checked={query.brand === brand.slug} onChange={(on) => onChange({ brand: on ? brand.slug : undefined })}>
              {brand.name}
            </Toggle>
          ))}
        </FilterGroup>
      ) : null}

      <FilterGroup title={t("availability")}>
        <Toggle checked={!!query.inStock} onChange={(on) => onChange({ inStock: on || undefined })}>
          {t("inStock")}
        </Toggle>
        <Toggle checked={!!query.onSale} onChange={(on) => onChange({ onSale: on || undefined })}>
          {t("onSale")}
        </Toggle>
      </FilterGroup>
    </div>
  );
}

/**
 * The product grid with sorting, filters, chips for what is applied, and "Load more". All the state lives in the address bar
 * (?sort=…&size=…), so a filtered view can be shared, bookmarked and reached with the back button.
 */
export function ProductListing({ category, searchPage = false, initialQuery, initialPage, initialFacets }: Props) {
  const t = useTranslations("store.listing");
  const tf = useTranslations("store.filters");
  const money = useMoney();
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const [sheetOpen, setSheetOpen] = React.useState(false);

  const query = React.useMemo(() => parseListQuery(params), [params]);
  const apiQuery: Omit<ProductQuery, "page" | "pageSize"> = { ...query, category };
  const initial = { query: { ...initialQuery, category } as Omit<ProductQuery, "page" | "pageSize">, page: initialPage };
  const products = useProductsInfinite(apiQuery, initial);
  const facets = useStoreFacets(category);
  const facetData = facets.data ?? initialFacets ?? undefined;

  const update = React.useCallback(
    (patch: Partial<ListQuery>) => {
      const next = toSearchParams({ ...query, ...patch });
      const qs = next.toString();
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false });
    },
    [query, pathname, router],
  );

  const items = products.data?.pages.flatMap((p) => p.items) ?? [];
  const total = Number(products.data?.pages[0]?.total ?? 0);
  const filterCount = activeFilterCount(query);

  const chips: { key: string; label: string; clear: Partial<ListQuery> }[] = [];
  if (query.minPrice !== undefined || query.maxPrice !== undefined) {
    const range = query.minPrice !== undefined && query.maxPrice !== undefined ? `${money(query.minPrice)} – ${money(query.maxPrice)}` : query.minPrice !== undefined ? tf("fromPrice", { price: money(query.minPrice) }) : tf("upToPrice", { price: money(query.maxPrice ?? 0) });
    chips.push({ key: "price", label: range, clear: { minPrice: undefined, maxPrice: undefined } });
  }
  if (query.size) chips.push({ key: "size", label: tf("sizeChip", { size: query.size }), clear: { size: undefined } });
  if (query.colour) chips.push({ key: "colour", label: query.colour, clear: { colour: undefined } });
  if (query.brand) chips.push({ key: "brand", label: facetData?.brands.find((b) => b.slug === query.brand)?.name ?? query.brand, clear: { brand: undefined } });
  if (query.inStock) chips.push({ key: "inStock", label: tf("inStock"), clear: { inStock: undefined } });
  if (query.onSale) chips.push({ key: "onSale", label: tf("onSale"), clear: { onSale: undefined } });

  const clearAll = () => update({ minPrice: undefined, maxPrice: undefined, size: undefined, colour: undefined, brand: undefined, inStock: undefined, onSale: undefined });

  return (
    <div className="lg:grid lg:grid-cols-[16rem_1fr] lg:gap-8">
      <aside className="hidden lg:block" aria-label={tf("title")}>
        <h2 className="mb-3 font-sans text-base font-semibold">{tf("title")}</h2>
        <FilterPanel key={params.toString()} query={query} facets={facetData} onChange={update} />
      </aside>

      <div className="min-w-0">
        <div className="mb-4 flex flex-wrap items-center gap-3">
          <p className="me-auto text-sm text-muted" aria-live="polite" data-testid="result-count">
            {products.isPending ? t("loading") : t("count", { count: total })}
          </p>

          <Dialog open={sheetOpen} onOpenChange={setSheetOpen}>
            <DialogTrigger asChild>
              <Button variant="secondary" className="lg:hidden">
                <SlidersHorizontal className="size-4" aria-hidden /> {tf("title")}
                {filterCount > 0 ? <span className="ms-1 rounded-full bg-primary px-2 text-xs text-on-primary">{filterCount}</span> : null}
              </Button>
            </DialogTrigger>
            <SheetContent side="bottom" closeLabel={tf("close")}>
              <div className="border-b border-line p-4">
                <DialogTitle className="font-sans text-lg font-semibold">{tf("title")}</DialogTitle>
                <DialogDescription className="sr-only">{tf("help")}</DialogDescription>
              </div>
              <div className="flex-1 overflow-y-auto p-4">
                {sheetOpen ? <FilterPanel key={params.toString()} query={query} facets={facetData} onChange={update} /> : null}
              </div>
              <div className="flex gap-3 border-t border-line p-4">
                {filterCount > 0 ? (
                  <Button variant="secondary" className="flex-1" onClick={clearAll}>
                    {tf("clear")}
                  </Button>
                ) : null}
                <Button className="flex-1" onClick={() => setSheetOpen(false)}>
                  {t("showResults", { count: total })}
                </Button>
              </div>
            </SheetContent>
          </Dialog>

          <label className="flex items-center gap-2 text-sm">
            <span className="text-muted">{t("sortBy")}</span>
            <select
              value={query.sort ?? "newest"}
              onChange={(e) => update({ sort: e.target.value === "newest" ? undefined : (e.target.value as Sort) })}
              className="h-11 rounded-control border border-line bg-card px-3 text-sm text-strong"
            >
              {SORT_OPTIONS.map((sort) => (
                <option key={sort} value={sort}>
                  {t(`sort.${sort}`)}
                </option>
              ))}
            </select>
          </label>
        </div>

        {chips.length > 0 ? (
          <ul className="mb-4 flex flex-wrap items-center gap-2" aria-label={tf("applied")}>
            {chips.map((chip) => (
              <li key={chip.key}>
                <button type="button" onClick={() => update(chip.clear)} className="inline-flex min-h-11 items-center gap-1.5 rounded-full border border-line bg-card ps-3.5 pe-2.5 text-sm text-strong hover:bg-sunken" aria-label={tf("removeFilter", { name: chip.label })}>
                  {chip.label}
                  <X className="size-4 text-muted" aria-hidden />
                </button>
              </li>
            ))}
            <li>
              <button type="button" onClick={clearAll} className="min-h-11 px-2 text-sm font-semibold text-primary hover:underline">
                {tf("clear")}
              </button>
            </li>
          </ul>
        ) : null}

        {products.isError ? (
          <Alert variant="danger" title={t("errorTitle")}>
            {t("errorBody")} <Button variant="link" onClick={() => void products.refetch()}>{t("retry")}</Button>
          </Alert>
        ) : products.isPending ? (
          <ProductGridSkeleton />
        ) : items.length === 0 ? (
          <EmptyState
            icon={<SearchX className="size-8" aria-hidden />}
            title={searchPage && query.q ? t("noSearchTitle", { q: query.q }) : t("emptyTitle")}
            description={searchPage ? t("noSearchBody") : filterCount > 0 ? t("emptyFiltered") : t("emptyBody")}
            action={
              filterCount > 0 ? (
                <Button onClick={clearAll}>{tf("clear")}</Button>
              ) : (
                <Button asChild>
                  <Link href="/shop">{t("browseAll")}</Link>
                </Button>
              )
            }
          />
        ) : (
          <>
            <div className={cn("transition-opacity", products.isPlaceholderData && "opacity-60")}>
              <ProductGrid products={items} priorityCount={4} />
            </div>
            <div className="mt-8 flex flex-col items-center gap-2">
              <p className="text-sm text-muted">{t("shown", { shown: items.length, total })}</p>
              {products.hasNextPage ? (
                <Button variant="secondary" size="lg" onClick={() => void products.fetchNextPage()} disabled={products.isFetchingNextPage}>
                  {products.isFetchingNextPage ? t("loadingMore") : t("loadMore")}
                </Button>
              ) : null}
            </div>
          </>
        )}
      </div>
    </div>
  );
}
