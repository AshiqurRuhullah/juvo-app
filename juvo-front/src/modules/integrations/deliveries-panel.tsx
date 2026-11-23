"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { RotateCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { toNumber } from "@/modules/catalog/format";
import { useAllWebhooks, useDeliveries, useRedeliver, type WebhookDelivery } from "./api";
import { DeliveryStatusBadge, useEventLabel, useIntegrationProblem } from "./common";
import { DELIVERY_STATUSES, LOG_EVENTS, anyDeliveryFilter, deliveryQuery, summariseRedelivery, type DeliveryFilters, type DeliveryStatus } from "./logic";

type Row = WebhookDelivery;

/**
 * The delivery log (newest first), filtered by endpoint, status and event, with Redeliver for failed deliveries (one or several).
 * Redelivery needs the manage permission; the API answers with the delivery as it is after being queued again, and that answer is
 * shown. Errors in the log are the API's scrubbed one-line sentences (never the address or the receiver's answer).
 */
export function DeliveriesPanel({ canRedeliver: canManage, endpointId, onEndpointChange }: { canRedeliver: boolean; endpointId: string | undefined; onEndpointChange: (id: string | undefined) => void }) {
  const t = useTranslations("integrations.log");
  const tc = useTranslations("integrations.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const eventLabel = useEventLabel();
  const problem = useIntegrationProblem();
  const state = useListState<{ status?: string; eventType?: string }>({});
  const endpoints = useAllWebhooks();
  const redeliver = useRedeliver();
  const [selected, setSelected] = React.useState<Set<string>>(new Set());
  const [confirm, setConfirm] = React.useState<Row[] | null>(null);
  const [answer, setAnswer] = React.useState<{ count: number; delivery: Row | null; refused: number } | null>(null);

  const filters: DeliveryFilters = { endpointId, status: state.filters.status, eventType: state.filters.eventType };
  const query = useDeliveries(deliveryQuery({ page: state.page, pageSize: state.pageSize, search: state.search }, filters));
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const failedOnPage = React.useMemo(() => items.filter((d) => d.status === "Failed"), [items]);
  const filtersActive = anyDeliveryFilter(filters, state.searchText);

  // Only rows that are on this page and still failed count (a stale id from another page is ignored).
  const chosen = React.useMemo(() => failedOnPage.filter((d) => selected.has(d.id)), [failedOnPage, selected]);

  const toggle = (id: string) =>
    setSelected((old) => {
      const next = new Set(old);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  const run = async (rows: Row[]) => {
    const outcomes: boolean[] = [];
    let last: Row | null = null;
    let firstProblem: string | null = null;
    for (const row of rows) {
      try {
        last = await redeliver.mutateAsync(row.id);
        outcomes.push(true);
      } catch (e) {
        outcomes.push(false);
        firstProblem ??= problem(e instanceof ApiError ? e.problem : null);
      }
    }
    setConfirm(null);
    setSelected(new Set());
    const { sent, refused } = summariseRedelivery(outcomes);
    if (refused > 0 && firstProblem) toast.error(firstProblem);
    setAnswer({ count: sent, delivery: rows.length === 1 ? last : null, refused });
  };

  const columns = React.useMemo<ColumnDef<Row, unknown>[]>(
    () => [
      ...(canManage
        ? [
            {
              id: "select",
              header: () => <span className="sr-only">{t("columns.select")}</span>,
              enableSorting: false,
              meta: { primary: true },
              cell: ({ row }: { row: { original: Row } }) =>
                row.original.status === "Failed" ? (
                  <label className="-m-2 inline-flex size-11 cursor-pointer items-center justify-center">
                    <Checkbox checked={selected.has(row.original.id)} onCheckedChange={() => toggle(row.original.id)} aria-label={t("selectRow", { event: row.original.eventType })} data-testid="delivery-select" />
                  </label>
                ) : null,
            } satisfies ColumnDef<Row, unknown>,
          ]
        : []),
      {
        id: "time",
        header: t("columns.time"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => <span className="text-sm">{dateTime(row.original.createdAt)}</span>,
      },
      {
        id: "endpoint",
        header: t("columns.endpoint"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <span className="[overflow-wrap:anywhere]" data-testid="delivery-endpoint">{row.original.endpointName ?? "-"}</span>,
      },
      {
        id: "event",
        header: t("columns.event"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block">
            <span className="block text-sm font-medium">{eventLabel(row.original.eventType)}</span>
            <span className="block font-mono text-xs text-muted" data-testid="delivery-event">
              {row.original.eventType}
            </span>
          </span>
        ),
      },
      {
        id: "attempt",
        header: t("columns.attempt"),
        enableSorting: false,
        meta: { secondary: true, align: "end" },
        cell: ({ row }) => <span className="tabular-nums">{t("attemptOf", { attempt: toNumber(row.original.attempt), max: toNumber(row.original.maxAttempts) })}</span>,
      },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <DeliveryStatusBadge status={row.original.status as DeliveryStatus} /> },
      {
        id: "http",
        header: t("columns.http"),
        enableSorting: false,
        meta: { secondary: true, align: "end" },
        cell: ({ row }) => <span className="tabular-nums" data-testid="delivery-http">{row.original.httpStatus === null || row.original.httpStatus === undefined ? "-" : toNumber(row.original.httpStatus)}</span>,
      },
      {
        id: "error",
        header: t("columns.error"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <span className="block max-w-xs text-sm text-muted [overflow-wrap:anywhere]" data-testid="delivery-error">{row.original.error ?? "-"}</span>,
      },
      {
        id: "next",
        header: t("columns.next"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => <span className="text-sm text-muted">{row.original.status === "Pending" ? dateTime(row.original.nextAttemptAt) : "-"}</span>,
      },
      ...(canManage
        ? [
            {
              id: "actions",
              header: "",
              enableSorting: false,
              meta: { actions: true, align: "end", label: t("columns.actions") },
              cell: ({ row }: { row: { original: Row } }) =>
                row.original.status === "Failed" ? (
                  <Button variant="secondary" onClick={() => setConfirm([row.original])} aria-label={t("redeliverLabel", { event: row.original.eventType })} data-testid="delivery-redeliver">
                    <RotateCw aria-hidden /> {t("redeliver")}
                  </Button>
                ) : null,
            } satisfies ColumnDef<Row, unknown>,
          ]
        : []),
    ],
    [t, dateTime, eventLabel, canManage, selected],
  );

  const endpointOptions = (endpoints.data ?? []).map((e) => ({ value: e.id, label: e.name }));
  const bulk = confirm !== null && confirm.length > 1;

  return (
    <section className="space-y-4" id="delivery-log" aria-labelledby="log-heading" data-testid="delivery-log">
      <div className="max-w-3xl">
        <h2 id="log-heading" className="font-display text-xl font-semibold text-strong">
          {t("title")}
        </h2>
        <p className="mt-1 text-muted">{t("intro")}</p>
      </div>

      <SearchBox className="lg:w-96" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <FilterSelect value={endpointId} onChange={(v) => { onEndpointChange(v); state.setPage(1); }} options={endpointOptions} allLabel={t("anyEndpoint")} label={t("columns.endpoint")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={DELIVERY_STATUSES.map((s) => ({ value: s, label: t(`status.${s}`) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.eventType} onChange={(v) => state.setFilter("eventType", v)} options={LOG_EVENTS.map((e) => ({ value: e, label: e }))} allLabel={t("anyEvent")} label={t("columns.event")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
      </div>
      {filtersActive ? (
        <Button
          variant="ghost"
          onClick={() => {
            state.resetFilters();
            onEndpointChange(undefined);
          }}
        >
          {tc("clear")}
        </Button>
      ) : null}

      {canManage && failedOnPage.length > 0 ? (
        <div className="flex flex-wrap items-center gap-3" data-testid="delivery-bulk">
          <Button variant="secondary" onClick={() => setSelected(new Set(failedOnPage.map((d) => d.id)))} data-testid="delivery-select-failed">
            {t("selectFailed", { count: failedOnPage.length })}
          </Button>
          {chosen.length > 0 ? (
            <>
              <span className="text-sm text-muted" data-testid="delivery-selected">
                {t("selected", { count: chosen.length })}
              </span>
              <Button onClick={() => setConfirm(chosen)} data-testid="delivery-redeliver-selected">
                <RotateCw aria-hidden /> {t("redeliverSelected")}
              </Button>
              <Button variant="ghost" onClick={() => setSelected(new Set())}>
                {t("clearSelection")}
              </Button>
            </>
          ) : null}
        </div>
      ) : null}

      {answer ? (
        <Alert variant={answer.refused > 0 ? "warning" : "success"} data-testid="redeliver-result">
          <span className="block space-y-1">
            <span className="block">{answer.count > 0 ? t("redeliverQueued", { count: answer.count }) : t("redeliverNone")}</span>
            {answer.delivery ? (
              <span className="flex flex-wrap items-center gap-2">
                <span>{t("redeliverNow")}</span>
                <DeliveryStatusBadge status={answer.delivery.status as DeliveryStatus} />
                {answer.delivery.error ? <span data-testid="redeliver-error">{answer.delivery.error}</span> : null}
              </span>
            ) : null}
            {answer.refused > 0 ? <span className="block">{t("redeliverRefused", { count: answer.refused })}</span> : null}
            <Button variant="ghost" onClick={() => setAnswer(null)} className="-ms-3">
              {tc("dismiss")}
            </Button>
          </span>
        </Alert>
      ) : null}

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={items}
        loading={query.isLoading}
        getRowId={(d) => d.id}
        labels={{ ...labels, empty: filtersActive ? labels.empty : t("empty"), emptyDescription: filtersActive ? labels.emptyDescription : t("emptyDescription") }}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
      />

      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => (open ? undefined : setConfirm(null))}
        title={bulk ? t("confirmBulkTitle", { count: confirm?.length ?? 0 }) : t("confirmTitle")}
        description={bulk ? t("confirmBulk", { count: confirm?.length ?? 0 }) : t("confirmOne")}
        confirmLabel={t("confirmAction")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={redeliver.isPending}
        onConfirm={() => (confirm ? run(confirm) : undefined)}
      />
    </section>
  );
}
