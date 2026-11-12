"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Badge, Alert } from "@/components/ui/surfaces";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { FilterSelect, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { dayRange } from "@/modules/orders/status";
import { REFUND_STATES, useRefunds, type Refund } from "./api";
import { usePaymentPermissions } from "./permissions";
import { RefundActions, RefundStateBadge } from "./refund-actions";

type Filters = { status?: string; from?: string; to?: string };

/** Every refund. A Pending one (paid by hand) can be completed (credit note issued) or failed from here. */
export function RefundsList() {
  const t = useTranslations("payments.admin.refunds");
  const tr = useTranslations("payments.refundState");
  const tm = useTranslations("payments.admin.detail.mode");
  const tc = useTranslations("payments.admin.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const perms = usePaymentPermissions();
  const state = useListState<Filters>({ status: undefined }, 20);
  const f = state.filters;
  const query = useRefunds({ page: state.page, pageSize: state.pageSize, status: f.status, ...dayRange(f.from, f.to) });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);

  const columns = React.useMemo<ColumnDef<Refund, unknown>[]>(
    () => [
      {
        id: "order",
        header: t("columns.order"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/payments/${row.original.paymentId}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="whitespace-nowrap font-mono">{row.original.orderNumber}</span>
          </Link>
        ),
      },
      { id: "amount", header: t("columns.amount"), meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="font-semibold tabular-nums text-strong">{money(row.original.amount)}</span> },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, cell: ({ row }) => <RefundStateBadge state={row.original.status} /> },
      { id: "mode", header: t("columns.mode"), meta: { secondary: true }, cell: ({ row }) => <Badge variant="outline">{tm(row.original.mode)}</Badge> },
      { id: "credit", header: t("columns.creditNote"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.creditNoteNumber ?? "-"}</span> },
      { id: "reason", header: t("columns.reason"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-sm text-muted [overflow-wrap:anywhere]">{row.original.reason ?? "-"}</span> },
      { id: "requested", header: t("columns.requested"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.requestedAt)}</span> },
      { id: "actions", header: t("columns.actions"), meta: { actions: true }, cell: ({ row }) => <RefundActions refund={row.original} canApprove={perms.refundApprove} /> },
    ],
    [t, tm, money, dateTime, perms.refundApprove],
  );

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-3">
        <FilterSelect value={f.status} onChange={(v) => state.setFilter("status", v)} options={REFUND_STATES.map((s) => ({ value: s, label: tr(s) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <Field label={t("from")} htmlFor="ref-from">
          <Input id="ref-from" type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="ref-to">
          <Input id="ref-to" type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
      </div>
      {f.status || f.from || f.to ? (
        <div>
          <Button variant="ghost" onClick={state.resetFilters}>
            {tc("clear")}
          </Button>
        </div>
      ) : null}
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} page={state.page} pageSize={state.pageSize} total={toNumber(query.data?.total)} onPageChange={state.setPage} />
    </div>
  );
}
