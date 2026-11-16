"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { useReceipts, type GoodsReceipt } from "./api";

const ReceiptDialog = dynamic(() => import("./receipt-dialog").then((m) => m.ReceiptDialog), { ssr: false });

type Filters = { branchId?: string; from?: string; to?: string };

/** The receipts tab: goods receipts newest first with filters; opening one shows its lines and lets a cost person apply the cost. */
export function ReceiptsView() {
  const t = useTranslations("purchasing.receipts");
  const tc = useTranslations("purchasing.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const branches = useBranchOptions();
  const state = useListState<Filters>({}, 20);
  const [openId, setOpenId] = React.useState<string | null>(null);

  const query = useReceipts({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    branchId: state.filters.branchId,
    from: state.filters.from ? `${state.filters.from}T00:00:00+06:00` : undefined,
    to: state.filters.to ? `${state.filters.to}T23:59:59+06:00` : undefined,
  });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data?.items]);

  const columns = React.useMemo<ColumnDef<GoodsReceipt, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.number"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <button type="button" onClick={() => setOpenId(row.original.id)} className="inline-flex min-h-11 min-w-11 items-center whitespace-nowrap rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="font-mono">{row.original.number}</span>
          </button>
        ),
      },
      {
        id: "order",
        header: t("columns.order"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <Link href={`/admin/purchasing/orders/${row.original.purchaseOrderId}`} className="inline-flex min-h-11 min-w-11 items-center whitespace-nowrap rounded-control font-mono text-sm text-muted hover:text-primary hover:underline">
            {row.original.purchaseOrderNumber}
          </Link>
        ),
      },
      { id: "at", header: t("columns.received"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="whitespace-nowrap text-muted">{dateTime(row.original.receivedAt)}</span> },
      { id: "by", header: t("columns.by"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span>{row.original.receivedByName ?? "-"}</span> },
      {
        id: "accepted",
        header: t("columns.accepted"),
        enableSorting: false,
        meta: { numeric: true, secondary: true },
        cell: ({ row }) => <span className="tabular-nums">{row.original.lines.reduce((n, l) => n + toNumber(l.acceptedQuantity), 0)}</span>,
      },
      {
        id: "rejected",
        header: t("columns.rejected"),
        enableSorting: false,
        meta: { numeric: true, hideOnMobile: true },
        cell: ({ row }) => <span className="tabular-nums">{row.original.lines.reduce((n, l) => n + toNumber(l.rejectedQuantity), 0)}</span>,
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button variant="secondary" onClick={() => setOpenId(row.original.id)} aria-label={t("openFor", { number: row.original.number })}>
            {t("view")}
          </Button>
        ),
      },
    ],
    [t, dateTime],
  );

  const filtersActive = !!state.searchText || Object.values(state.filters).some(Boolean);

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted" aria-live="polite">
        {t("total", { count: toNumber(query.data?.total) })}
      </p>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        {branches.length > 1 ? (
          <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={tc("from")} htmlFor="rc-from">
          <Input id="rc-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor="rc-to">
          <Input id="rc-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
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
      <DataTable caption={t("title")} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} page={state.page} pageSize={state.pageSize} total={toNumber(query.data?.total)} onPageChange={state.setPage} />
      {openId ? <ReceiptDialog id={openId} onClose={() => setOpenId(null)} /> : null}
    </div>
  );
}
