"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { CheckCircle2, Printer } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, ChoiceRow, Field, Input } from "@/components/ui/form";
import { LiveBadge } from "@/components/ui/live-badge";
import { Alert, Badge } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { usePermissions } from "@/components/providers/session-provider";
import { useLiveRefresh, type LiveEventName } from "@/lib/realtime/use-live";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { COURIER_KEYS, useCouriers, useShipments, type ShipmentListItem } from "./api";
import { activeFilterCount, labelsHref } from "./admin-logic";
import { ShipmentStatusBadge } from "./common";
import { useCourierPermissions } from "./permissions";
import { SHIPMENT_STATUSES, formatWeight } from "./status";

type Filters = { status?: string; courierId?: string; kind?: string; branchId?: string; from?: string; to?: string };

const LIVE_KEYS = [COURIER_KEYS.shipments];
const LIVE_EVENTS: LiveEventName[] = ["ShipmentEventAdded"];
const MAX_LABELS = 50;
const INITIAL_SORT = [{ id: "bookedAt", desc: true }];

/** `/admin/shipments`: every parcel with search, filters, server-side paging and sorting, live refresh and bulk label printing. */
export function ShipmentsList() {
  const t = useTranslations("shipments.list");
  const ts = useTranslations("shipments.status");
  const tk = useTranslations("shipments.kind");
  const tc = useTranslations("shipments.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const money = useMoney();
  const branches = useBranchOptions();
  const perms = useCourierPermissions();
  const { activeBranchId, isAllBranches } = usePermissions();
  const state = useListState<Filters>({}, undefined, INITIAL_SORT);
  const [codOnly, setCodOnly] = React.useState(false);
  const [openOnly, setOpenOnly] = React.useState(false);
  const [selected, setSelected] = React.useState<ReadonlySet<string>>(new Set());

  const query = useShipments({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    sort: state.sort,
    status: state.filters.status,
    courierId: state.filters.courierId,
    branchId: state.filters.branchId,
    kind: state.filters.kind as "Forward" | "Return" | undefined,
    codOnly,
    open: openOnly,
    from: state.filters.from,
    to: state.filters.to,
  });

  // A new event on any parcel of the active branch (or of every branch for global staff) refreshes the list: no polling.
  const live = useLiveRefresh({ mode: "staff", branch: isAllBranches ? "all" : activeBranchId, queryKeys: LIVE_KEYS, events: LIVE_EVENTS });

  const items = React.useMemo(() => query.data?.items ?? [], [query.data?.items]);
  const canSelect = perms.printLabel;

  const toggle = React.useCallback((id: string, on: boolean) => {
    setSelected((old) => {
      const next = new Set(old);
      if (on) {
        if (next.size < MAX_LABELS) next.add(id);
      } else next.delete(id);
      return next;
    });
  }, []);
  const pageIds = React.useMemo(() => items.map((i) => i.id), [items]);
  const allOnPage = pageIds.length > 0 && pageIds.every((id) => selected.has(id));
  const someOnPage = pageIds.some((id) => selected.has(id));

  const columns = React.useMemo<ColumnDef<ShipmentListItem, unknown>[]>(
    () => [
      ...(canSelect
        ? ([
            {
              id: "select",
              header: () => (
                <label className="-m-2 inline-flex size-11 cursor-pointer items-center justify-center">
                  <Checkbox
                    aria-label={t("selectAll")}
                    checked={allOnPage ? true : someOnPage ? "indeterminate" : false}
                    onCheckedChange={(v) =>
                      setSelected((old) => {
                        const next = new Set(old);
                        for (const id of pageIds) {
                          if (v === true) {
                            if (next.size < MAX_LABELS) next.add(id);
                          } else next.delete(id);
                        }
                        return next;
                      })
                    }
                  />
                </label>
              ),
              enableSorting: false,
              meta: { label: t("selectColumn"), hideOnMobile: false },
              cell: ({ row }) => (
                <label className="-m-2 inline-flex size-11 cursor-pointer items-center justify-center">
                  <Checkbox aria-label={t("selectRow", { number: row.original.consignmentId })} checked={selected.has(row.original.id)} onCheckedChange={(v) => toggle(row.original.id, v === true)} />
                </label>
              ),
            },
          ] as ColumnDef<ShipmentListItem, unknown>[])
        : []),
      {
        id: "consignment",
        header: t("columns.consignment"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/shipments/${row.original.id}`} className="inline-flex min-h-11 min-w-11 items-center whitespace-nowrap rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="font-mono">{row.original.consignmentId}</span>
          </Link>
        ),
      },
      {
        id: "status",
        accessorFn: (row) => row.status,
        header: t("columns.status"),
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="flex flex-wrap items-center gap-1.5">
            <ShipmentStatusBadge status={row.original.status} />
            {row.original.kind === "Return" ? <Badge variant="outline">{tk("Return")}</Badge> : null}
          </span>
        ),
      },
      {
        id: "order",
        accessorFn: (row) => row.orderNumber,
        header: t("columns.order"),
        meta: { secondary: true },
        cell: ({ row }) => (
          <Link href={`/admin/orders/${row.original.orderId}`} className="inline-flex min-h-11 min-w-11 items-center whitespace-nowrap rounded-control font-mono text-sm text-muted hover:text-primary hover:underline">
            {row.original.orderNumber}
          </Link>
        ),
      },
      { id: "courier", header: t("columns.courier"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span>{row.original.courierName}</span> },
      {
        id: "recipient",
        header: t("columns.recipient"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block [overflow-wrap:anywhere]">
            <span className="block font-medium text-strong">{row.original.deliveryName}</span>
            {row.original.deliveryDistrict ? <span className="block text-sm text-muted">{row.original.deliveryDistrict}</span> : null}
          </span>
        ),
      },
      {
        id: "cod",
        header: t("columns.cod"),
        enableSorting: false,
        meta: { numeric: true, secondary: true },
        cell: ({ row }) => {
          const due = toNumber(row.original.codAmount);
          const got = row.original.codCollectedAmount === null ? null : toNumber(row.original.codCollectedAmount);
          if (due <= 0) return <span className="text-muted">{t("noCod")}</span>;
          return (
            <span className="inline-flex flex-col items-end">
              <span className="font-medium tabular-nums text-strong">{money(due)}</span>
              {got !== null ? (
                <span className="inline-flex items-center gap-1 text-xs text-success-ink">
                  <CheckCircle2 className="size-3.5" aria-hidden /> {t("collected", { amount: money(got) })}
                </span>
              ) : null}
            </span>
          );
        },
      },
      { id: "weight", header: t("columns.weight"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{formatWeight(toNumber(row.original.weightGrams))}</span> },
      { id: "bookedAt", accessorFn: (row) => row.bookedAt, header: t("columns.booked"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="whitespace-nowrap text-muted">{dateTime(row.original.bookedAt)}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/shipments/${row.original.id}`} aria-label={t("open", { number: row.original.consignmentId })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, tk, dateTime, money, canSelect, selected, allOnPage, someOnPage, pageIds, toggle],
  );

  const flags = { codOnly, open: openOnly };
  const filterCount = activeFilterCount(state.filters, flags);
  const filtersActive = !!state.searchText || filterCount > 0;
  const clearAll = () => {
    state.resetFilters();
    setCodOnly(false);
    setOpenOnly(false);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted" aria-live="polite" data-testid="shipment-total">
          {t("total", { count: toNumber(query.data?.total) })}
        </p>
        <LiveBadge status={live} />
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2 xl:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={SHIPMENT_STATUSES.map((x) => ({ value: x, label: ts(x) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {perms.courierView ? <CourierFilter value={state.filters.courierId} onChange={(v) => state.setFilter("courierId", v)} /> : null}
        <FilterSelect value={state.filters.kind} onChange={(v) => state.setFilter("kind", v)} options={[{ value: "Forward", label: tk("Forward") }, { value: "Return", label: tk("Return") }]} allLabel={t("anyKind")} label={t("kind")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {branches.length > 1 ? (
          <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={t("from")} htmlFor="sh-from">
          <Input id="sh-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="sh-to">
          <Input id="sh-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
        <div className="flex flex-wrap items-center gap-x-6 sm:col-span-2 xl:col-span-2">
          <ChoiceRow htmlFor="sh-cod">
            <Checkbox id="sh-cod" checked={codOnly} onCheckedChange={(v) => setCodOnly(v === true)} />
            {t("codOnly")}
          </ChoiceRow>
          <ChoiceRow htmlFor="sh-open">
            <Checkbox id="sh-open" checked={openOnly} onCheckedChange={(v) => setOpenOnly(v === true)} />
            {t("openOnly")}
          </ChoiceRow>
          {filtersActive ? (
            <Button variant="ghost" onClick={clearAll}>
              {tc("clear")}
            </Button>
          ) : null}
        </div>
      </div>

      {canSelect && selected.size > 0 ? (
        <div className="flex flex-wrap items-center gap-2 rounded-card border border-primary/30 bg-primary-soft p-3" role="region" aria-label={t("bulkLabel")} data-testid="bulk-bar">
          <p className="me-auto font-semibold text-strong" aria-live="polite">
            {t("selected", { count: selected.size })}
            {selected.size >= MAX_LABELS ? <span className="ms-2 text-sm font-normal text-muted">{t("selectedMax", { max: MAX_LABELS })}</span> : null}
          </p>
          <Button asChild>
            <Link href={labelsHref([...selected])} data-testid="print-labels">
              <Printer aria-hidden /> {t("printLabels")}
            </Link>
          </Button>
          <Button variant="ghost" onClick={() => setSelected(new Set())}>
            {t("clearSelection")}
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

/** Mounted only for people who may see couriers, so the courier list is not asked for by everyone. */
function CourierFilter({ value, onChange }: { value: string | undefined; onChange: (v: string | undefined) => void }) {
  const t = useTranslations("shipments.list");
  const tc = useTranslations("shipments.common");
  const couriers = useCouriers();
  const options = React.useMemo(() => (couriers.data ?? []).map((c) => ({ value: c.id, label: c.name })), [couriers.data]);
  return <FilterSelect value={value} onChange={onChange} options={options} allLabel={t("anyCourier")} label={t("columns.courier")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />;
}
