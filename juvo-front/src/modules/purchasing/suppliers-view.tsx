"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useSuppliers, type Supplier } from "./api";
import { usePurchasingPermissions } from "./permissions";

const SupplierDialog = dynamic(() => import("./supplier-dialog").then((m) => m.SupplierDialog), { ssr: false });

type Filters = { active?: string };
const INITIAL_SORT = [{ id: "name", desc: false }];

/** The suppliers tab: list with search and an active filter, create and edit in a dialog, a link to each supplier's purchase orders. */
export function SuppliersView() {
  const t = useTranslations("purchasing.suppliers");
  const tc = useTranslations("purchasing.common");
  const labels = useTableLabels();
  const perms = usePurchasingPermissions();
  const state = useListState<Filters>({}, undefined, INITIAL_SORT);
  const [editing, setEditing] = React.useState<Supplier | null>(null);
  const [open, setOpen] = React.useState(false);

  const query = useSuppliers({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    sort: state.sort,
    active: state.filters.active === undefined ? undefined : state.filters.active === "yes",
  });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data?.items]);

  const edit = React.useCallback((s: Supplier | null) => {
    setEditing(s);
    setOpen(true);
  }, []);

  const columns = React.useMemo<ColumnDef<Supplier, unknown>[]>(
    () => [
      { id: "name", accessorFn: (r) => r.name, header: t("columns.name"), meta: { primary: true }, cell: ({ row }) => <span className="font-semibold text-strong [overflow-wrap:anywhere]">{row.original.name}</span> },
      {
        id: "contact",
        header: t("columns.contact"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block [overflow-wrap:anywhere]">
            {row.original.contactName ?? "-"}
            {row.original.phone ? <span className="block text-sm text-muted">{row.original.phone}</span> : null}
          </span>
        ),
      },
      { id: "terms", header: t("columns.terms"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{t("days", { count: toNumber(row.original.paymentTermsDays) })}</span> },
      { id: "open", header: t("columns.open"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.openOrders)}</span> },
      {
        id: "status",
        header: t("columns.status"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <Badge variant={row.original.isActive ? "success" : "outline"}>{row.original.isActive ? t("active") : t("inactive")}</Badge>,
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <span className="flex flex-wrap justify-end gap-2">
            {perms.poView ? (
              <Button asChild variant="secondary">
                <Link href={`/admin/purchasing?tab=orders&supplierId=${row.original.id}`} aria-label={t("historyFor", { name: row.original.name })}>
                  {t("history")}
                </Link>
              </Button>
            ) : null}
            {perms.supplierEdit ? (
              <Button variant="secondary" onClick={() => edit(row.original)} aria-label={t("editFor", { name: row.original.name })}>
                {t("edit")}
              </Button>
            ) : null}
          </span>
        ),
      },
    ],
    [t, perms.poView, perms.supplierEdit, edit],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted" aria-live="polite">
          {t("total", { count: toNumber(query.data?.total) })}
        </p>
        {perms.supplierCreate ? (
          <Button onClick={() => edit(null)} data-testid="new-supplier">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.active} onChange={(v) => state.setFilter("active", v)} options={[{ value: "yes", label: t("active") }, { value: "no", label: t("inactive") }]} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
      </div>
      <Alert variant="info">{t("historyNote")}</Alert>
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
      {open ? <SupplierDialog open={open} onOpenChange={setOpen} supplier={editing} /> : null}
    </div>
  );
}
