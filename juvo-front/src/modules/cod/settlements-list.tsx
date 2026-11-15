"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Banknote, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { useBranchOptions } from "@/modules/inventory/common";
import { useCouriers, useSettlements, type SettlementListItem } from "@/modules/couriers/api";
import { useCourierPermissions } from "@/modules/couriers/permissions";
import { SETTLEMENT_STATUSES } from "@/modules/couriers/status";
import { Difference, SettlementStatusBadge, useBranchLabel, useMoney2 } from "./common";
import { num } from "./logic";
import { useCashAtCouriers, useStatusSummary } from "./queries";

type Filters = { status?: string; courierId?: string; branchId?: string };

/** `/admin/cod`: the cash waiting at couriers, the batches by status, filters and the list (cards on phones). */
export function SettlementsList() {
  const t = useTranslations("cod.list");
  const ts = useTranslations("cod.status");
  const tc = useTranslations("cod.common");
  const tn = useTranslations("nav");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const money = useMoney2();
  const branchLabel = useBranchLabel();
  const branches = useBranchOptions();
  const perms = useCourierPermissions();
  const state = useListState<Filters>({}, 20);
  const couriers = useCouriers();

  const query = useSettlements({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    status: state.filters.status,
    courierId: state.filters.courierId,
    branchId: state.filters.branchId,
  });
  const items = query.data?.items ?? [];

  const columns = React.useMemo<ColumnDef<SettlementListItem, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.number"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/cod/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="font-mono">{row.original.number}</span>
          </Link>
        ),
      },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <SettlementStatusBadge status={row.original.status} /> },
      { id: "courier", header: t("columns.courier"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="font-medium text-strong [overflow-wrap:anywhere]">{row.original.courierName}</span> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{branchLabel(row.original.branchId).code}</span> },
      { id: "parcels", header: t("columns.parcels"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{num(row.original.parcels)}</span> },
      { id: "expected", header: t("columns.expected"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="font-medium tabular-nums">{money(row.original.expectedTotal)}</span> },
      { id: "received", header: t("columns.received"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.receivedTotal)}</span> },
      { id: "difference", header: t("columns.difference"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <Difference difference={row.original.difference} /> },
      { id: "created", header: t("columns.created"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}</span> },
      { id: "settled", header: t("columns.settled"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.settledAt)}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/cod/${row.original.id}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, money, dateTime, branchLabel],
  );

  const filtersActive = !!(state.searchText || Object.values(state.filters).some(Boolean));
  const noBatchesAtAll = !query.isLoading && !query.isError && num(query.data?.total) === 0 && !filtersActive;

  return (
    <>
      <PageHeader
        title={t("title")}
        description={t("subtitle")}
        crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]}
        actions={
          perms.codSettle ? (
            <Button asChild data-testid="new-batch">
              <Link href="/admin/cod/new">
                <Plus aria-hidden /> {t("newBatch")}
              </Link>
            </Button>
          ) : null
        }
      />
      <div className="space-y-4">
        <Summary branchId={state.filters.branchId} courierId={state.filters.courierId} statusFilter={state.filters.status} onStatus={(s) => state.setFilter("status", s)} />

        <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
          <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
          <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={SETTLEMENT_STATUSES.map((x) => ({ value: x, label: ts(x) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          {couriers.data && couriers.data.length > 0 ? (
            <FilterSelect value={state.filters.courierId} onChange={(v) => state.setFilter("courierId", v)} options={couriers.data.map((c) => ({ value: c.id, label: c.name }))} allLabel={t("anyCourier")} label={t("columns.courier")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          ) : null}
          {branches.length > 1 ? (
            <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={t("columns.branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          ) : null}
          {filtersActive ? (
            <div className="flex items-end">
              <Button variant="ghost" onClick={state.resetFilters}>
                {tc("clear")}
              </Button>
            </div>
          ) : null}
        </div>

        {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

        {noBatchesAtAll ? (
          <EmptyState
            icon={<Banknote />}
            title={t("emptyTitle")}
            description={perms.codSettle ? t("emptyHintSettle") : t("emptyHint")}
            action={
              perms.codSettle ? (
                <Button asChild>
                  <Link href="/admin/cod/new">
                    <Plus aria-hidden /> {t("newBatch")}
                  </Link>
                </Button>
              ) : null
            }
          />
        ) : (
          <DataTable
            caption={t("title")}
            columns={columns}
            data={items}
            loading={query.isLoading}
            getRowId={(x) => x.id}
            labels={labels}
            page={state.page}
            pageSize={state.pageSize}
            total={num(query.data?.total)}
            onPageChange={state.setPage}
          />
        )}
      </div>
    </>
  );
}

/** Cash waiting at couriers, unsettled batches and the status chips (a chip filters the list). Reads run in parallel. */
function Summary({ branchId, courierId, statusFilter, onStatus }: { branchId: string | undefined; courierId: string | undefined; statusFilter: string | undefined; onStatus: (status: string | undefined) => void }) {
  const t = useTranslations("cod.summary");
  const ts = useTranslations("cod.status");
  const money = useMoney2();
  const cash = useCashAtCouriers(branchId);
  const summary = useStatusSummary(branchId, courierId);

  return (
    <section aria-label={t("label")} className="space-y-3" data-testid="cod-summary">
      <div className="grid gap-2 sm:grid-cols-2">
        <div className="min-h-11 rounded-card border border-line bg-card p-3" data-testid="cash-at-couriers">
          <p className="text-sm text-muted">{t("atCouriers")}</p>
          {cash.pending ? (
            <Skeleton className="mt-1 h-8 w-40" aria-busy="true" />
          ) : (
            <>
              <p className="text-2xl font-bold tabular-nums text-strong" data-testid="cash-total">
                {money(cash.amount)}
              </p>
              <p className="text-sm text-muted">{cash.parcels === 0 ? t("nothingWaiting") : t("parcelsWaiting", { count: cash.parcels })}</p>
              {cash.rows.length > 0 ? (
                <ul className="mt-2 space-y-0.5 text-sm" aria-label={t("byCourier")}>
                  {cash.rows.map((r) => (
                    <li key={r.courier.id} className="flex items-baseline justify-between gap-3" data-testid="cash-courier">
                      <span className="text-strong [overflow-wrap:anywhere]">{r.courier.name}</span>
                      <span className="tabular-nums text-muted">{t("courierLine", { parcels: r.parcels, amount: money(r.amount) })}</span>
                    </li>
                  ))}
                </ul>
              ) : null}
            </>
          )}
        </div>
        <div className="min-h-11 rounded-card border border-line bg-card p-3" data-testid="unsettled">
          <p className="text-sm text-muted">{t("unsettled")}</p>
          {summary.pending ? (
            <Skeleton className="mt-1 h-8 w-40" aria-busy="true" />
          ) : (
            <>
              <p className="text-2xl font-bold tabular-nums text-strong">
                {summary.partial ? "≥ " : ""}
                {money(summary.unsettledTotal)}
              </p>
              <p className="text-sm text-muted">{t("unsettledBatches", { count: summary.unsettledCount })}</p>
            </>
          )}
        </div>
      </div>

      <ul className="grid grid-cols-2 gap-2 md:grid-cols-4" aria-label={t("byStatus")}>
        {SETTLEMENT_STATUSES.map((status) => {
          const active = statusFilter === status;
          return (
            <li key={status}>
              <button
                type="button"
                aria-pressed={active}
                data-testid={`chip-${status}`}
                onClick={() => onStatus(active ? undefined : status)}
                className={cn("min-h-11 w-full rounded-card border bg-card p-3 text-start transition-colors", active ? "border-primary bg-primary-soft" : "border-line [@media(hover:hover)]:hover:border-strong/30")}
              >
                {summary.pending ? <Skeleton className="h-8 w-10" /> : <span className="block text-2xl font-bold tabular-nums text-strong">{summary.counts[status]}</span>}
                <span className="block text-sm text-muted">{ts(status)}</span>
              </button>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
