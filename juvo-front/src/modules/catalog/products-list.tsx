"use client";

import * as React from "react";
import Image from "next/image";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { ImageOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { PRODUCT_STATUSES, useAllBrands, useCategoryTree, useProducts, type ProductListItem, type ProductStatus } from "./api";
import { flattenCategories } from "./category-options";
import { renditionUrl, toNumber, useMoney } from "./format";

/** Products: search, filters, status, price and a thumbnail; each row opens the editor. Import, export and "New product" sit in the page header. */
export function ProductsList({ initialSearch }: { initialSearch?: string }) {
  const t = useTranslations("catalog.list");
  const ts = useTranslations("catalog.status");
  const labels = useTableLabels();
  const money = useMoney();
  const dateTime = useDateTime();
  const state = useListState<{ status?: string; categoryId?: string; brandId?: string }>({}, 20, [{ id: "updatedAt", desc: true }]);
  const categories = useCategoryTree(true);
  const brands = useAllBrands();

  const initialised = React.useRef(false);
  React.useEffect(() => {
    if (!initialised.current && initialSearch) {
      initialised.current = true;
      state.setSearchText(initialSearch);
    }
  }, [initialSearch, state]);

  const query = useProducts({
    page: state.page,
    pageSize: state.pageSize,
    sort: state.sort,
    search: state.search,
    status: state.filters.status as ProductStatus | undefined,
    categoryId: state.filters.categoryId,
    brandId: state.filters.brandId,
  });

  const columns = React.useMemo<ColumnDef<ProductListItem, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.product"),
        meta: { primary: true },
        cell: ({ row }) => {
          const p = row.original;
          const thumb = renditionUrl(p.primaryImagePath, "thumb");
          return (
            <Link href={`/admin/products/${p.id}`} className="flex min-h-11 min-w-56 items-center gap-3 rounded-control font-medium text-strong hover:text-primary">
              <span className="relative block h-14 w-11 shrink-0 overflow-hidden rounded-md border border-line bg-sunken">
                {thumb ? <Image src={thumb} alt="" fill unoptimized sizes="44px" className="object-cover" /> : <ImageOff className="absolute inset-0 m-auto size-5 text-muted" aria-hidden />}
              </span>
              <span className="min-w-0">
                <span className="block [overflow-wrap:anywhere] hover:underline">{p.nameEn}</span>
                <span className="block text-sm font-normal text-muted [overflow-wrap:anywhere]">{p.slug}</span>
              </span>
            </Link>
          );
        },
      },
      { id: "category", header: t("columns.category"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.categoryName}</span> },
      {
        id: "price",
        header: t("columns.price"),
        meta: { numeric: true, secondary: true },
        cell: ({ row }) => {
          const p = row.original;
          const onSale = toNumber(p.effectivePrice) < toNumber(p.regularPrice);
          return (
            <span className="flex flex-wrap items-baseline justify-end gap-x-2">
              <span className="font-semibold">{money(p.effectivePrice)}</span>
              {onSale ? <span className="text-sm text-muted line-through">{money(p.regularPrice)}</span> : null}
            </span>
          );
        },
      },
      {
        id: "status",
        header: t("columns.status"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => {
          const p = row.original;
          return (
            <span className="flex flex-wrap items-center justify-end gap-1.5 md:justify-start">
              <Badge variant={p.status === "Published" ? "success" : p.status === "Archived" ? "outline" : "neutral"}>{ts(p.status)}</Badge>
              {p.isNew ? <Badge variant="brand">{t("new")}</Badge> : null}
              {p.isFeatured ? <Badge variant="info">{t("featured")}</Badge> : null}
              {p.isBestSeller ? <Badge variant="warning">{t("bestSeller")}</Badge> : null}
            </span>
          );
        },
      },
      {
        id: "variants",
        header: t("columns.variants"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span>
            {t("variantsPictures", { variants: toNumber(row.original.variantCount), pictures: toNumber(row.original.imageCount) })}
          </span>
        ),
      },
      { id: "updatedAt", header: t("columns.updated"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.updatedAt)}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/products/${row.original.id}`} aria-label={t("open", { name: row.original.nameEn })}>
              {t("edit")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, ts, money, dateTime],
  );

  const filtersActive = !!(state.searchText || state.filters.status || state.filters.categoryId || state.filters.brandId);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 xl:flex-row xl:items-center">
        <SearchBox className="xl:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <div className="grid flex-1 gap-3 sm:grid-cols-3">
          <FilterSelect
            value={state.filters.status}
            onChange={(v) => state.setFilter("status", v)}
            options={PRODUCT_STATUSES.map((s) => ({ value: s, label: ts(s) }))}
            allLabel={t("filters.anyStatus")}
            label={t("filters.status")}
            searchPlaceholder={t("filters.search")}
            emptyText={t("filters.noMatch")}
          />
          <FilterSelect
            value={state.filters.categoryId}
            onChange={(v) => state.setFilter("categoryId", v)}
            options={flattenCategories(categories.data ?? []).map((c) => ({ value: c.id, label: c.label }))}
            allLabel={t("filters.anyCategory")}
            label={t("filters.category")}
            searchPlaceholder={t("filters.search")}
            emptyText={t("filters.noMatch")}
          />
          <FilterSelect
            value={state.filters.brandId}
            onChange={(v) => state.setFilter("brandId", v)}
            options={(brands.data?.items ?? []).map((b) => ({ value: b.id, label: b.nameEn }))}
            allLabel={t("filters.anyBrand")}
            label={t("filters.brand")}
            searchPlaceholder={t("filters.search")}
            emptyText={t("filters.noMatch")}
          />
        </div>
        <div className="flex flex-wrap gap-2">
          {filtersActive ? (
            <Button variant="ghost" onClick={state.resetFilters}>
              {t("filters.clear")}
            </Button>
          ) : null}
        </div>
      </div>

      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(p) => p.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
        sorting={state.sorting}
        onSortingChange={state.setSorting}
      />
    </div>
  );
}
