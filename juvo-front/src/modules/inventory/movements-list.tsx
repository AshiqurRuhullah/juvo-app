"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import Link from "next/link";
import { FilterSelect, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { ALL_REASONS, useMovements, type StockMovement, type StockReason } from "./api";
import { VariantText, useBranchOptions, useReasonLabel } from "./common";

/** Dhaka is UTC+6 all year: a chosen day starts at 00:00 and ends at 23:59:59 there. */
const dayStart = (day: string) => `${day}T00:00:00+06:00`;
const dayEnd = (day: string) => `${day}T23:59:59+06:00`;

/** The append-only stock ledger: every change of every number, with who, when and why. */
export function MovementsList() {
  const t = useTranslations("inventory.movements");
  const tc = useTranslations("inventory.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const reasonLabel = useReasonLabel();
  const branches = useBranchOptions();
  const state = useListState<{ branchId?: string; reason?: string; from?: string; to?: string }>({}, 20);

  const query = useMovements({
    page: state.page,
    pageSize: state.pageSize,
    branchId: state.filters.branchId,
    reason: state.filters.reason as StockReason | undefined,
    from: state.filters.from ? dayStart(state.filters.from) : undefined,
    to: state.filters.to ? dayEnd(state.filters.to) : undefined,
  });

  const columns = React.useMemo<ColumnDef<StockMovement, unknown>[]>(
    () => [
      { id: "createdAt", header: t("columns.date"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="font-semibold text-strong">{dateTime(row.original.createdAt)}</span> },
      {
        id: "product",
        header: t("columns.product"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <div className="min-w-0 py-1">
            <span className="font-semibold text-strong [overflow-wrap:anywhere]">{row.original.productName}</span>
            <VariantText sku={row.original.sku} />
          </div>
        ),
      },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant="outline">{row.original.branchCode}</Badge> },
      {
        id: "change",
        header: t("columns.change"),
        enableSorting: false,
        meta: { align: "end", secondary: true },
        cell: ({ row }) => {
          const change = toNumber(row.original.quantityChange);
          return (
            <span className={`font-semibold tabular-nums ${change < 0 ? "text-danger-ink" : "text-success-ink"}`}>
              {change > 0 ? "+" : ""}
              {change}
              <span className="sr-only"> {change < 0 ? t("removed") : t("added")}</span>
            </span>
          );
        },
      },
      { id: "after", header: t("columns.after"), enableSorting: false, meta: { align: "end", hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.onHandAfter)}</span> },
      { id: "reason", header: t("columns.reason"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant="neutral">{reasonLabel(row.original.reason)}</Badge> },
      {
        id: "note",
        header: t("columns.note"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => <span className="text-muted [overflow-wrap:anywhere]">{row.original.note ?? "-"}</span>,
      },
      {
        id: "reference",
        header: t("columns.reference"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => {
          const { referenceType, referenceId } = row.original;
          if (!referenceId || (referenceType !== "Transfer" && referenceType !== "Count")) return <span className="text-muted">-</span>;
          const href = referenceType === "Transfer" ? `/admin/inventory/transfers/${referenceId}` : `/admin/inventory/counts/${referenceId}`;
          return (
            <Link href={href} className="inline-flex min-h-11 items-center font-semibold text-primary hover:underline">
              {t(referenceType === "Transfer" ? "reference.transfer" : "reference.count")}
            </Link>
          );
        },
      },
      { id: "createdBy", header: t("columns.by"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted [overflow-wrap:anywhere]">{row.original.createdByName ?? t("system")}</span> },
    ],
    [t, dateTime, reasonLabel],
  );

  const filtersActive = !!(state.filters.branchId || state.filters.reason || state.filters.from || state.filters.to);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
        <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect
          value={state.filters.reason}
          onChange={(v) => state.setFilter("reason", v)}
          options={ALL_REASONS.map((r) => ({ value: r, label: reasonLabel(r) }))}
          allLabel={t("anyReason")}
          label={t("reason")}
          searchPlaceholder={tc("search")}
          emptyText={tc("noMatch")}
        />
        <Field label={t("from")} htmlFor="mv-from">
          <Input id="mv-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="mv-to">
          <Input id="mv-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
      </div>
      {filtersActive ? (
        <Button variant="ghost" onClick={state.resetFilters}>
          {tc("clear")}
        </Button>
      ) : null}

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(m) => m.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
      />
    </div>
  );
}
