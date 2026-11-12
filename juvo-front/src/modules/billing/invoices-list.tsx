"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { dayRange } from "@/modules/orders/status";
import { useCreditNotes, useInvoices, type CreditNote, type InvoiceListItem } from "./api";

type Filters = { branchId?: string; from?: string; to?: string };

/** Search, branch and date filters shared by the invoice and the credit note lists. */
function ListFilters({ state, searchPlaceholder, idPrefix }: { state: ReturnType<typeof useListState<Filters>>; searchPlaceholder: string; idPrefix: string }) {
  const tc = useTranslations("billing.common");
  const branches = useBranchOptions();
  const f = state.filters;
  const active = !!(state.searchText || f.branchId || f.from || f.to);
  return (
    <div className="space-y-3">
      <SearchBox className="lg:w-96" value={state.searchText} onChange={state.setSearchText} placeholder={searchPlaceholder} label={tc("search")} />
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {branches.length > 1 ? (
          <FilterSelect value={f.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={tc("from")} htmlFor={`${idPrefix}-from`}>
          <Input id={`${idPrefix}-from`} type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor={`${idPrefix}-to`}>
          <Input id={`${idPrefix}-to`} type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
        {active ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>
    </div>
  );
}

export function InvoicesList() {
  const t = useTranslations("billing.invoices");
  const tc = useTranslations("billing.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const state = useListState<Filters>({}, 20, [{ id: "issuedAt", desc: true }]);
  const range = dayRange(state.filters.from, state.filters.to);
  const query = useInvoices({ page: state.page, pageSize: state.pageSize, sort: state.sort, search: state.search, branchId: state.filters.branchId, ...range });

  const columns = React.useMemo<ColumnDef<InvoiceListItem, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.number"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/billing/invoices/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline" data-testid="invoice-link">
            <span className="font-mono [overflow-wrap:anywhere]">{row.original.number}</span>
          </Link>
        ),
      },
      { id: "order", header: t("columns.order"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="font-mono text-sm [overflow-wrap:anywhere]">{row.original.orderNumber}</span> },
      { id: "customer", header: t("columns.customer"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.customerName}</span> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "total", header: t("columns.total"), enableSorting: false, meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="font-semibold tabular-nums text-strong">{money(row.original.grandTotal)}</span> },
      { id: "tax", header: t("columns.tax"), enableSorting: false, meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.taxTotal)}</span> },
      { id: "issuedAt", header: t("columns.issued"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.issuedAt)}</span> },
    ],
    [t, money, dateTime],
  );

  return (
    <div className="space-y-4">
      <ListFilters state={state} searchPlaceholder={t("searchPlaceholder")} idPrefix="inv" />
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
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

export function CreditNotesList() {
  const t = useTranslations("billing.credit");
  const tc = useTranslations("billing.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const state = useListState<Filters>({}, 20, [{ id: "issuedAt", desc: true }]);
  const range = dayRange(state.filters.from, state.filters.to);
  const query = useCreditNotes({ page: state.page, pageSize: state.pageSize, sort: state.sort, search: state.search, branchId: state.filters.branchId, ...range });

  const columns = React.useMemo<ColumnDef<CreditNote, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.number"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/billing/credit-notes/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline" data-testid="credit-note-link">
            <span className="font-mono [overflow-wrap:anywhere]">{row.original.number}</span>
          </Link>
        ),
      },
      { id: "invoice", header: t("columns.invoice"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="font-mono text-sm [overflow-wrap:anywhere]">{row.original.invoiceNumber}</span> },
      { id: "order", header: t("columns.order"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="font-mono text-sm [overflow-wrap:anywhere]">{row.original.orderNumber}</span> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "amount", header: t("columns.amount"), enableSorting: false, meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="font-semibold tabular-nums text-strong">{money(row.original.amount)}</span> },
      { id: "tax", header: t("columns.tax"), enableSorting: false, meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.taxAmount)}</span> },
      { id: "issuedAt", header: t("columns.issued"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.issuedAt)}</span> },
    ],
    [t, money, dateTime],
  );

  return (
    <div className="space-y-4">
      <ListFilters state={state} searchPlaceholder={t("searchPlaceholder")} idPrefix="cn" />
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
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
