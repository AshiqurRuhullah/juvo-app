"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Hourglass, Paperclip } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { PaymentStatusBadge } from "@/modules/orders/common";
import { dayRange } from "@/modules/orders/status";
import { PAYMENT_KINDS, PAYMENT_STATUSES, usePayments, type PaymentListItem } from "./api";
import { usePayMethodLabel } from "./common";

export const METHOD_CODES = ["COD", "SANDBOX", "MANUAL", "CASH", "BKASH", "NAGAD", "SSLCOMMERZ", "AAMARPAY", "STRIPE", "PAYPAL"];

type Filters = { status?: string; method?: string; kind?: string; branchId?: string; from?: string; to?: string };

/** Every payment: online, manual transfer, cash on delivery and advances, with the transfer review queue one tap away. */
export function PaymentsList() {
  const t = useTranslations("payments.admin.list");
  const ts = useTranslations("orders.status.payment");
  const tk = useTranslations("payments.kind");
  const tc = useTranslations("payments.admin.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const methodLabel = usePayMethodLabel();
  const branches = useBranchOptions();
  const state = useListState<Filters>({}, 20, [{ id: "createdAt", desc: true }]);
  const f = state.filters;
  const range = dayRange(f.from, f.to);
  const query = usePayments({ page: state.page, pageSize: state.pageSize, search: state.search, sort: state.sort, ...f, ...range });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const filtersActive = !!(state.searchText || Object.values(f).some(Boolean));
  const queueActive = f.status === "UnderReview";

  const columns = React.useMemo<ColumnDef<PaymentListItem, unknown>[]>(
    () => [
      {
        id: "orderNumber",
        header: t("columns.order"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/payments/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline" data-testid="payment-link">
            <span className="font-mono [overflow-wrap:anywhere]">{row.original.orderNumber}</span>
          </Link>
        ),
      },
      { id: "kind", header: t("columns.kind"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant="outline">{tk(row.original.kind)}</Badge> },
      { id: "method", header: t("columns.method"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="text-strong">{methodLabel(row.original.method)}</span> },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, cell: ({ row }) => <PaymentStatusBadge status={row.original.status} /> },
      { id: "amount", header: t("columns.amount"), meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="font-semibold tabular-nums text-strong">{money(row.original.amount)}</span> },
      {
        id: "reference",
        header: t("columns.reference"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => (
          <span className="flex items-center gap-1 text-sm text-muted [overflow-wrap:anywhere]">
            {row.original.submittedReference ?? "-"}
            {row.original.hasProof ? <Paperclip className="size-4 shrink-0" aria-label={t("hasProof")} /> : null}
          </span>
        ),
      },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "createdAt", header: t("columns.created"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}</span> },
    ],
    [t, tk, money, dateTime, methodLabel],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
        <SearchBox className="lg:w-96" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <div className="flex-1" />
        <button
          type="button"
          aria-pressed={queueActive}
          onClick={() => state.setFilter("status", queueActive ? undefined : "UnderReview")}
          className={cn("inline-flex min-h-11 items-center gap-2 rounded-full border px-4 text-sm font-medium transition-colors", queueActive ? "border-primary bg-primary-soft text-strong" : "border-line bg-card text-muted hover:text-strong")}
          data-testid="review-queue"
        >
          <Hourglass className="size-4" aria-hidden /> {t("queue")}
        </button>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <FilterSelect value={f.status} onChange={(v) => state.setFilter("status", v)} options={PAYMENT_STATUSES.map((s) => ({ value: s, label: ts(s) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.method} onChange={(v) => state.setFilter("method", v)} options={METHOD_CODES.map((m) => ({ value: m, label: methodLabel(m) }))} allLabel={t("anyMethod")} label={t("columns.method")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.kind} onChange={(v) => state.setFilter("kind", v)} options={PAYMENT_KINDS.map((k) => ({ value: k, label: tk(k) }))} allLabel={t("anyKind")} label={t("columns.kind")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {branches.length > 1 ? (
          <FilterSelect value={f.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={t("anyBranch")} label={t("columns.branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={t("from")} htmlFor="pay-from">
          <Input id="pay-from" type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="pay-to">
          <Input id="pay-to" type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
      </div>

      {filtersActive ? (
        <div>
          <Button variant="ghost" onClick={state.resetFilters}>
            {tc("clear")}
          </Button>
        </div>
      ) : null}

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
