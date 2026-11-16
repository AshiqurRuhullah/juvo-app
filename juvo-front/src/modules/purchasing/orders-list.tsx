"use client";

import * as React from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { usePurchaseOrders, useSupplierOptions, type PurchaseListItem } from "./api";
import { PayStatusBadge, PoStatusBadge, useDateOnly, useMoney2 } from "./common";
import { PAYMENT_STATUSES, PO_STATUSES } from "./logic";
import { usePurchasingPermissions } from "./permissions";

type Filters = { status?: string; paymentStatus?: string; supplierId?: string; branchId?: string; from?: string; to?: string };
const INITIAL_SORT = [{ id: "createdAt", desc: true }];

/** The purchase orders tab: search, filters, server paging and sorting; cards on phones. */
export function OrdersList() {
  const t = useTranslations("purchasing.list");
  const tc = useTranslations("purchasing.common");
  const ts = useTranslations("purchasing.status");
  const tp = useTranslations("purchasing.payStatus");
  const labels = useTableLabels();
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const branches = useBranchOptions();
  const perms = usePurchasingPermissions();
  const params = useSearchParams();
  const state = useListState<Filters>({ supplierId: params.get("supplierId") ?? undefined }, 20, INITIAL_SORT);

  const query = usePurchaseOrders({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    sort: state.sort,
    ...state.filters,
    from: state.filters.from ? `${state.filters.from}T00:00:00+06:00` : undefined,
    to: state.filters.to ? `${state.filters.to}T23:59:59+06:00` : undefined,
  });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data?.items]);

  const columns = React.useMemo<ColumnDef<PurchaseListItem, unknown>[]>(
    () => [
      {
        id: "number",
        accessorFn: (r) => r.number,
        header: t("columns.number"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/purchasing/orders/${row.original.id}`} className="inline-flex min-h-11 min-w-11 items-center rounded-control font-semibold text-strong [overflow-wrap:anywhere] hover:text-primary hover:underline md:whitespace-nowrap">
            <span className="font-mono">{row.original.number}</span>
          </Link>
        ),
      },
      { id: "supplier", header: t("columns.supplier"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.supplierName}</span> },
      { id: "status", accessorFn: (r) => r.status, header: t("columns.status"), meta: { secondary: true }, cell: ({ row }) => <PoStatusBadge status={row.original.status} /> },
      {
        id: "payment",
        header: t("columns.payment"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <PayStatusBadge status={row.original.paymentStatus} overdue={row.original.isOverdue} />,
      },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      {
        id: "expected",
        accessorFn: (r) => r.expectedDate,
        header: t("columns.expected"),
        meta: { hideOnMobile: true },
        cell: ({ row }) => <span className="whitespace-nowrap text-muted">{dateOnly(row.original.expectedDate)}</span>,
      },
      {
        id: "units",
        header: t("columns.units"),
        enableSorting: false,
        meta: { numeric: true, hideOnMobile: true },
        cell: ({ row }) => <span className="tabular-nums">{t("unitsOf", { received: toNumber(row.original.receivedQuantity), ordered: toNumber(row.original.orderedQuantity) })}</span>,
      },
      { id: "total", header: t("columns.total"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="font-medium tabular-nums text-strong">{money(row.original.total)}</span> },
      {
        id: "outstanding",
        header: t("columns.outstanding"),
        enableSorting: false,
        meta: { numeric: true, hideOnMobile: true },
        cell: ({ row }) => <span className="tabular-nums">{money(row.original.outstanding)}</span>,
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/purchasing/orders/${row.original.id}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, money, dateOnly],
  );

  const filtersActive = !!state.searchText || Object.values(state.filters).some(Boolean);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted" aria-live="polite" data-testid="po-total">
          {t("total", { count: toNumber(query.data?.total) })}
        </p>
        {perms.poCreate ? (
          <Button asChild>
            <Link href="/admin/purchasing/orders/new" data-testid="new-po">
              <Plus aria-hidden /> {t("new")}
            </Link>
          </Button>
        ) : null}
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={PO_STATUSES.map((x) => ({ value: x, label: ts(x) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.paymentStatus} onChange={(v) => state.setFilter("paymentStatus", v)} options={PAYMENT_STATUSES.map((x) => ({ value: x, label: tp(x) }))} allLabel={t("anyPayment")} label={t("columns.payment")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {perms.supplierView ? <SupplierFilter value={state.filters.supplierId} onChange={(v) => state.setFilter("supplierId", v)} /> : null}
        {branches.length > 1 ? (
          <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={tc("from")} htmlFor="po-from">
          <Input id="po-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor="po-to">
          <Input id="po-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={items}
        loading={query.isLoading}
        getRowId={(x) => x.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
        sorting={state.sorting}
        onSortingChange={state.setSorting}
      />
    </div>
  );
}

/** Mounted only for people who may see suppliers, so the supplier list is not asked for by everyone. */
function SupplierFilter({ value, onChange }: { value: string | undefined; onChange: (v: string | undefined) => void }) {
  const t = useTranslations("purchasing.list");
  const tc = useTranslations("purchasing.common");
  const suppliers = useSupplierOptions();
  const options = React.useMemo(() => (suppliers.data?.items ?? []).map((s) => ({ value: s.id, label: s.name })), [suppliers.data?.items]);
  return <FilterSelect value={value} onChange={onChange} options={options} allLabel={t("anySupplier")} label={t("columns.supplier")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />;
}
