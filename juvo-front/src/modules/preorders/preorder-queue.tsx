"use client";

import * as React from "react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Hourglass, PackageCheck, PackagePlus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { usePermissions } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { useOrderPermissions } from "@/modules/orders/permissions";
import { PREORDER_STATES, useAllocatePreorders, usePreorderQueue, type PreorderLine, type PreorderQueueItem } from "./api";
import { allocateSummary, formatShipDate } from "./logic";

type Filters = { state?: string; branchId?: string; expectedBefore?: string };

export function StateBadge({ state }: { state: string }) {
  const t = useTranslations("preorders.queue.state");
  return state === "Ready" ? (
    <Badge variant="success">
      <PackageCheck className="size-3" aria-hidden /> {t("Ready")}
    </Badge>
  ) : (
    <Badge variant="warning">
      <Hourglass className="size-3" aria-hidden /> {t("Waiting")}
    </Badge>
  );
}

/** One line of a queue order with its own state: still waiting for units, or served on a date. Words, never colour alone. */
function LineState({ line }: { line: PreorderLine }) {
  const t = useTranslations("preorders.queue");
  const dateTime = useDateTime();
  const locale = useLocale();
  const waiting = toNumber(line.waiting);
  return (
    <li className="space-y-0.5" data-testid="queue-line" data-sku={line.sku}>
      <span className="block font-medium text-strong [overflow-wrap:anywhere]">
        {line.name} × {toNumber(line.quantity)}
      </span>
      <span className="block text-xs text-muted">
        <span className="font-mono">{line.sku}</span>
        {[line.colorName, line.size].filter(Boolean).length > 0 ? ` · ${[line.colorName, line.size].filter(Boolean).join(" · ")}` : ""}
      </span>
      <span className="block text-xs">
        {waiting > 0 ? (
          <span className="font-medium text-warning-ink">{t("lineWaiting", { count: waiting })}</span>
        ) : (
          <span className="font-medium text-success-ink">{line.allocatedAt ? t("lineServed", { date: dateTime(line.allocatedAt) }) : t("lineServedNoDate")}</span>
        )}
        {line.expectedShipDate ? <span className="text-muted"> · {t("expected", { date: formatShipDate(line.expectedShipDate, locale) })}</span> : null}
      </span>
    </li>
  );
}

/** `/admin/orders/preorders` (FR-AA03): orders with lines that wait for stock or just got it, oldest first, with the allocate action. */
export function PreorderQueue() {
  const t = useTranslations("preorders.queue");
  const tc = useTranslations("shipments.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const money = useMoney();
  const problem = useProblemMessage();
  const branches = useBranchOptions();
  const perms = useOrderPermissions();
  const { activeBranchId, isAllBranches } = usePermissions();
  const state = useListState<Filters>({});
  const allocate = useAllocatePreorders();

  const query = usePreorderQueue({ page: state.page, pageSize: state.pageSize, search: state.search, state: state.filters.state, branchId: state.filters.branchId, expectedBefore: state.filters.expectedBefore });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data?.items]);
  const filtersActive = !!state.searchText || Object.values(state.filters).some(Boolean);

  const runAllocate = async () => {
    // The branch asked for: the filter when one is chosen, else the branch the person works in, else every branch they may serve.
    const branchId = state.filters.branchId ?? (isAllBranches ? null : (activeBranchId ?? null));
    try {
      const result = allocateSummary(await allocate.mutateAsync({ branchId, variantId: null }));
      if (result.kind === "none") toast(t("allocateNone"));
      else toast.success(t("allocateDone", { lines: result.lines, orders: result.orders }));
    } catch (failure) {
      toast.error(problem(failure instanceof ApiError ? failure.problem : null));
    }
  };

  const columns = React.useMemo<ColumnDef<PreorderQueueItem, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.order"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/orders/${row.original.orderId}`} className="inline-flex min-h-11 min-w-11 items-center whitespace-nowrap rounded-control font-mono font-semibold text-strong hover:text-primary hover:underline" data-testid="queue-order">
            {row.original.number}
          </Link>
        ),
      },
      { id: "state", header: t("columns.state"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <StateBadge state={row.original.state} /> },
      {
        id: "customer",
        header: t("columns.customer"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block [overflow-wrap:anywhere]">
            <span className="block font-medium text-strong">{row.original.customerName ?? t("noName")}</span>
            <span className="block text-sm text-muted">{row.original.customerMobile}</span>
          </span>
        ),
      },
      {
        id: "lines",
        header: t("columns.lines"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <ul className="space-y-2 text-sm">{row.original.lines.map((l) => <LineState key={l.itemId} line={l} />)}</ul>,
      },
      {
        id: "advance",
        header: t("columns.advance"),
        enableSorting: false,
        meta: { numeric: true, hideOnMobile: true },
        cell: ({ row }) => {
          const due = toNumber(row.original.advanceRequired);
          return due > 0 ? <span className="tabular-nums">{t("advanceOf", { paid: money(row.original.advancePaid), due: money(due) })}</span> : <span className="text-muted">{t("noAdvance")}</span>;
        },
      },
      { id: "total", header: t("columns.total"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="font-medium tabular-nums text-strong">{money(row.original.grandTotal)}</span> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "placed", header: t("columns.placed"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="whitespace-nowrap text-muted">{dateTime(row.original.placedAt)}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/orders/${row.original.orderId}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, money, dateTime],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted" aria-live="polite" data-testid="queue-total">
          {t("total", { count: toNumber(query.data?.total) })}
        </p>
        {perms.edit ? (
          <Button onClick={() => void runAllocate()} loading={allocate.isPending} data-testid="allocate">
            <PackagePlus aria-hidden /> {t("allocate")}
          </Button>
        ) : null}
      </div>
      <Alert variant="info">{t("rule")}</Alert>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.state} onChange={(v) => state.setFilter("state", v)} options={PREORDER_STATES.map((s) => ({ value: s, label: t(`state.${s}`) }))} allLabel={t("anyState")} label={t("columns.state")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {branches.length > 1 ? <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} /> : null}
        <Field label={t("expectedBefore")} htmlFor="pq-before">
          <Input id="pq-before" type="date" value={state.filters.expectedBefore ?? ""} onChange={(e) => state.setFilter("expectedBefore", e.target.value || undefined)} />
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
        getRowId={(x) => x.orderId}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
      />
    </div>
  );
}
