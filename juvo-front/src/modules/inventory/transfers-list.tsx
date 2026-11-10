"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { ArrowRight, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert } from "@/components/ui/surfaces";
import { Can } from "@/components/providers/session-provider";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { TRANSFER_STATUSES, useTransfers, type Transfer, type TransferStatus } from "./api";
import { TransferStatusBadge, useBranchOptions } from "./common";

/** Inter-branch stock transfers visible to this person (they hold the view permission in either branch). */
export function TransfersList() {
  const t = useTranslations("inventory.transfers");
  const ts = useTranslations("inventory.transferStatus");
  const tc = useTranslations("inventory.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const branches = useBranchOptions();
  const state = useListState<{ status?: string; branchId?: string }>({}, 20);

  const query = useTransfers({ page: state.page, pageSize: state.pageSize, search: state.search, status: state.filters.status as TransferStatus | undefined, branchId: state.filters.branchId });

  const columns = React.useMemo<ColumnDef<Transfer, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.number"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/inventory/transfers/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="font-mono">{row.original.number}</span>
          </Link>
        ),
      },
      {
        id: "route",
        header: t("columns.route"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="inline-flex items-center gap-2 font-semibold text-strong">
            {row.original.fromBranchCode}
            <ArrowRight className="size-4 text-muted rtl:rotate-180" aria-hidden />
            <span className="sr-only">{t("to")}</span>
            {row.original.toBranchCode}
          </span>
        ),
      },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <TransferStatusBadge status={row.original.status} /> },
      { id: "lines", header: t("columns.lines"), enableSorting: false, meta: { align: "end", hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{row.original.lines.length}</span> },
      { id: "createdAt", header: t("columns.created"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/inventory/transfers/${row.original.id}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, dateTime],
  );

  const filtersActive = !!(state.searchText || state.filters.status || state.filters.branchId);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
        <SearchBox className="lg:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <div className="grid flex-1 gap-3 sm:grid-cols-2">
          <FilterSelect
            value={state.filters.status}
            onChange={(v) => state.setFilter("status", v)}
            options={TRANSFER_STATUSES.map((s) => ({ value: s, label: ts(s) }))}
            allLabel={t("anyStatus")}
            label={t("columns.status")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
          {branches.length > 1 ? (
            <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          ) : null}
        </div>
        <div className="flex gap-2">
          {filtersActive ? (
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          ) : null}
          <Can permission="inventory.transfer.request" scope="anywhere">
            <Button asChild>
              <Link href="/admin/inventory/transfers/new">
                <Plus aria-hidden /> {t("new")}
              </Link>
            </Button>
          </Can>
        </div>
      </div>

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
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
      />
    </div>
  );
}
