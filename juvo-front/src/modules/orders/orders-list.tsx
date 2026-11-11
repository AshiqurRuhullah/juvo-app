"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { CircleAlert, CircleCheck, Plus, Printer } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, Field, Input, Label, Textarea } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import {
  DELIVERY_STATUSES,
  FRAUD_STATES,
  ORDER_SOURCES,
  ORDER_STATUSES,
  PAYMENT_STATUSES,
  useBulkAction,
  useOrderErrorText,
  useOrders,
  useSourceSummary,
  type BulkResult,
  type DeliveryStatus,
  type FraudState,
  type OrderListItem,
  type OrderSource,
  type OrderStatus,
  type PaymentStatus,
} from "./api";
import { DeliveryStatusBadge, FraudBadge, OrderStatusBadge, PaymentStatusBadge, useSourceLabel } from "./common";
import { useOrderPermissions } from "./permissions";
import { dayRange } from "./status";

type Filters = { status?: string; paymentStatus?: string; deliveryStatus?: string; source?: string; fraudState?: string; branchId?: string; from?: string; to?: string; held?: string };

/** The order list: filters for every track, branch, source, fraud state and dates; bulk actions; source summary chips. */
export function OrdersList() {
  const t = useTranslations("orders.list");
  const ts = useTranslations("orders.status");
  const tc = useTranslations("orders.common");
  const router = useRouter();
  const money = useMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const sourceLabel = useSourceLabel();
  const branches = useBranchOptions();
  const errorText = useOrderErrorText();
  const perms = useOrderPermissions();
  const state = useListState<Filters>({}, 20);
  const bulk = useBulkAction();

  const [selected, setSelected] = React.useState<ReadonlySet<string>>(new Set());
  const [cancelOpen, setCancelOpen] = React.useState(false);
  const [reason, setReason] = React.useState("");
  const [reasonError, setReasonError] = React.useState<string | null>(null);
  const [advanceOpen, setAdvanceOpen] = React.useState(false);
  const [advance, setAdvance] = React.useState(false);
  const [result, setResult] = React.useState<BulkResult | null>(null);

  const range = dayRange(state.filters.from, state.filters.to);
  const query = useOrders({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    sort: state.sort,
    status: state.filters.status as OrderStatus | undefined,
    paymentStatus: state.filters.paymentStatus as PaymentStatus | undefined,
    deliveryStatus: state.filters.deliveryStatus as DeliveryStatus | undefined,
    source: state.filters.source as OrderSource | undefined,
    fraudState: state.filters.fraudState as FraudState | undefined,
    branchId: state.filters.branchId,
    held: state.filters.held === "1" ? true : undefined,
    ...range,
  });
  const summary = useSourceSummary(range, perms.sources);
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);

  const toggle = (id: string, on: boolean) =>
    setSelected((old) => {
      const next = new Set(old);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  const pageIds = items.map((o) => o.id);
  const allOnPage = pageIds.length > 0 && pageIds.every((id) => selected.has(id));
  const toggleAll = (on: boolean) =>
    setSelected((old) => {
      const next = new Set(old);
      for (const id of pageIds) {
        if (on) next.add(id);
        else next.delete(id);
      }
      return next;
    });

  const run = async (action: "confirm" | "cancel", extra: { reason?: string; advanceReceived?: boolean } = {}) => {
    try {
      const answer = await bulk.mutateAsync({ action, ids: [...selected], ...extra });
      setResult(answer);
      setSelected(new Set());
      toast.success(t("bulk.done", { ok: toNumber(answer.succeeded), failed: toNumber(answer.failed) }));
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  const columns = React.useMemo<ColumnDef<OrderListItem, unknown>[]>(
    () => [
      {
        id: "number",
        header: t("columns.number"),
        meta: { primary: true },
        cell: ({ row }) => {
          const o = row.original;
          return (
            <span className="flex items-start gap-1">
              {perms.bulk ? (
                <label className="-ms-2 flex size-11 shrink-0 cursor-pointer items-center justify-center">
                  <Checkbox checked={selected.has(o.id)} onCheckedChange={(v) => toggle(o.id, v === true)} aria-label={t("select", { number: o.number })} />
                </label>
              ) : null}
              <span className="min-w-0 space-y-1">
                <Link href={`/admin/orders/${o.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline" data-testid="order-link">
                  <span className="font-mono [overflow-wrap:anywhere]">{o.number}</span>
                </Link>
                <span className="block">
                  <FraudBadge state={o.fraudState} />
                </span>
              </span>
            </span>
          );
        },
      },
      {
        id: "customer",
        header: t("columns.customer"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block">
            <span className="block font-medium text-strong [overflow-wrap:anywhere]">{row.original.customerName ?? "-"}</span>
            <span className="block text-sm text-muted">{row.original.customerMobile}</span>
          </span>
        ),
      },
      { id: "source", header: t("columns.source"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant="outline">{sourceLabel(row.original.source)}</Badge> },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, cell: ({ row }) => <OrderStatusBadge status={row.original.status} /> },
      { id: "payment", header: t("columns.payment"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <PaymentStatusBadge status={row.original.paymentStatus} /> },
      { id: "delivery", header: t("columns.delivery"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <DeliveryStatusBadge status={row.original.deliveryStatus} /> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "total", header: t("columns.total"), meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="font-semibold tabular-nums text-strong">{money(row.original.grandTotal)}</span> },
      { id: "placedAt", header: t("columns.placed"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.placedAt)}</span> },
    ],
    [t, money, dateTime, sourceLabel, perms.bulk, selected],
  );

  const f = state.filters;
  const filtersActive = !!(state.searchText || Object.values(f).some(Boolean));

  return (
    <div className="space-y-4">
      {perms.sources && summary.data && summary.data.length > 0 ? (
        <ul className="flex flex-wrap gap-2" aria-label={t("sources")} data-testid="source-chips">
          {summary.data.map((s) => (
            <li key={s.source}>
              <button
                type="button"
                aria-pressed={f.source === s.source}
                onClick={() => state.setFilter("source", f.source === s.source ? undefined : s.source)}
                className={cn("min-h-11 rounded-full border px-4 text-sm font-medium transition-colors", f.source === s.source ? "border-primary bg-primary-soft text-strong" : "border-line bg-card text-muted hover:text-strong")}
              >
                {sourceLabel(s.source)}: <span className="text-strong">{toNumber(s.orders)}</span> · {money(s.total)}
              </button>
            </li>
          ))}
        </ul>
      ) : null}

      <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
        <SearchBox className="lg:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <div className="flex-1" />
        {perms.create ? (
          <Button asChild>
            <Link href="/admin/orders/new">
              <Plus aria-hidden /> {t("newOrder")}
            </Link>
          </Button>
        ) : null}
      </div>

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <FilterSelect value={f.status} onChange={(v) => state.setFilter("status", v)} options={ORDER_STATUSES.map((s) => ({ value: s, label: ts(`order.${s}`) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.paymentStatus} onChange={(v) => state.setFilter("paymentStatus", v)} options={PAYMENT_STATUSES.map((s) => ({ value: s, label: ts(`payment.${s}`) }))} allLabel={t("anyPayment")} label={t("columns.payment")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.deliveryStatus} onChange={(v) => state.setFilter("deliveryStatus", v)} options={DELIVERY_STATUSES.map((s) => ({ value: s, label: ts(`delivery.${s}`) }))} allLabel={t("anyDelivery")} label={t("columns.delivery")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.source} onChange={(v) => state.setFilter("source", v)} options={ORDER_SOURCES.map((s) => ({ value: s, label: sourceLabel(s) }))} allLabel={t("anySource")} label={t("columns.source")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.fraudState} onChange={(v) => state.setFilter("fraudState", v)} options={FRAUD_STATES.map((s) => ({ value: s, label: ts(`fraud.${s}`) }))} allLabel={t("anyFraud")} label={t("fraud")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {branches.length > 1 ? (
          <FilterSelect value={f.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={t("anyBranch")} label={t("columns.branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={t("from")} htmlFor="orders-from">
          <Input id="orders-from" type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="orders-to">
          <Input id="orders-to" type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
      </div>

      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-4">
          <div className="flex min-h-11 items-center gap-2">
            <Checkbox id="orders-held" checked={f.held === "1"} onCheckedChange={(v) => state.setFilter("held", v === true ? "1" : undefined)} />
            <Label htmlFor="orders-held" className="flex min-h-11 cursor-pointer items-center">
              {t("heldOnly")}
            </Label>
          </div>
          {perms.bulk && items.length > 0 ? (
            <div className="flex min-h-11 items-center gap-2">
              <Checkbox id="orders-all" checked={allOnPage} onCheckedChange={(v) => toggleAll(v === true)} />
              <Label htmlFor="orders-all" className="flex min-h-11 cursor-pointer items-center">
                {t("selectPage")}
              </Label>
            </div>
          ) : null}
        </div>
        {filtersActive ? (
          <Button variant="ghost" onClick={state.resetFilters}>
            {tc("clear")}
          </Button>
        ) : null}
      </div>

      {perms.bulk && selected.size > 0 ? (
        <div role="region" aria-label={t("bulk.title")} className="sticky top-16 z-10 flex flex-wrap items-center gap-2 rounded-card border border-primary bg-primary-soft p-3" data-testid="bulk-bar">
          <p className="me-auto font-semibold text-strong" aria-live="polite" data-testid="bulk-count">
            {t("bulk.selected", { count: selected.size })}
          </p>
          {perms.confirm ? (
            <Button
              loading={bulk.isPending}
              onClick={() => {
                setAdvance(false);
                setAdvanceOpen(true);
              }}
              data-testid="bulk-confirm"
            >
              <CircleCheck aria-hidden /> {t("bulk.confirm")}
            </Button>
          ) : null}
          {perms.cancel ? (
            <Button variant="secondary" onClick={() => setCancelOpen(true)} data-testid="bulk-cancel">
              {t("bulk.cancel")}
            </Button>
          ) : null}
          {perms.print ? (
            <Button variant="secondary" onClick={() => router.push(`/admin/orders/print?ids=${[...selected].join(",")}`)} data-testid="bulk-print">
              <Printer aria-hidden /> {t("bulk.print")}
            </Button>
          ) : null}
          <Button variant="ghost" onClick={() => setSelected(new Set())}>
            {t("bulk.clear")}
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

      <ConfirmDialog
        open={advanceOpen}
        onOpenChange={setAdvanceOpen}
        title={t("bulk.confirmTitle", { count: selected.size })}
        description={t("bulk.confirmText")}
        confirmLabel={t("bulk.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={bulk.isPending}
        onConfirm={async () => {
          await run("confirm", { advanceReceived: advance });
          setAdvanceOpen(false);
        }}
      >
        <div className="flex min-h-11 items-start gap-3">
          <Checkbox id="bulk-advance" checked={advance} onCheckedChange={(v) => setAdvance(v === true)} className="mt-0.5" />
          <Label htmlFor="bulk-advance" className="flex-1 cursor-pointer py-2.5 font-normal">
            {t("bulk.advance")}
          </Label>
        </div>
      </ConfirmDialog>

      <ConfirmDialog
        open={cancelOpen}
        onOpenChange={(open) => {
          setCancelOpen(open);
          if (!open) setReasonError(null);
        }}
        title={t("bulk.cancelTitle", { count: selected.size })}
        description={t("bulk.cancelText")}
        confirmLabel={t("bulk.cancel")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={bulk.isPending}
        onConfirm={async () => {
          if (!reason.trim()) return setReasonError(t("bulk.reasonRequired"));
          await run("cancel", { reason: reason.trim() });
          setCancelOpen(false);
          setReason("");
        }}
      >
        <Field label={t("bulk.reason")} htmlFor="bulk-reason" required error={reasonError ?? undefined}>
          <Textarea id="bulk-reason" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} aria-invalid={!!reasonError} />
        </Field>
      </ConfirmDialog>

      <Dialog open={!!result} onOpenChange={(open) => (open ? undefined : setResult(null))}>
        <ModalContent size="md" closeLabel={tc("close")}>
          <DialogTitle className="font-display text-xl font-semibold text-strong">{t("bulk.resultTitle")}</DialogTitle>
          <DialogDescription className="mt-1 text-muted" data-testid="bulk-result">
            {result ? t("bulk.done", { ok: toNumber(result.succeeded), failed: toNumber(result.failed) }) : ""}
          </DialogDescription>
          <ul className="mt-4 max-h-80 space-y-2 overflow-y-auto" aria-label={t("bulk.resultTitle")}>
            {(result?.results ?? []).map((r) => (
              <li key={r.orderId} className="flex items-start gap-2 text-sm">
                {r.ok ? <CircleCheck className="mt-0.5 size-5 shrink-0 text-success" aria-hidden /> : <CircleAlert className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden />}
                <span className="min-w-0">
                  <span className="font-mono font-semibold text-strong">{r.number ?? r.orderId}</span> <span className="text-muted">{r.ok ? t("bulk.ok") : (r.message ?? r.code)}</span>
                </span>
              </li>
            ))}
          </ul>
          <div className="mt-6 flex justify-end">
            <Button onClick={() => setResult(null)}>{tc("close")}</Button>
          </div>
        </ModalContent>
      </Dialog>
    </div>
  );
}
