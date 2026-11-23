"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { RotateCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { cn } from "@/lib/utils";
import { toNumber } from "@/modules/catalog/format";
import { useOutbox, useOutboxSummary, useRequeueOutbox, type OutboxMessage, type RequeueResultDto } from "./api";
import { BulkBar } from "./bulk-bar";
import { ErrorText, QueueStateBadge, ResultNotice } from "./common";
import { OUTBOX_STATES, anyFilter, canRequeue, outboxQuery, type OutboxFilters, type OutboxState } from "./logic";
import { useSelection } from "./use-selection";

/**
 * The queue of background messages that are not finished. Chips filter by state (counts come from the summary), a dead-lettered or
 * retrying message can be requeued (one or up to 100 ticked rows) after a confirmation that says what will happen. The payload is
 * never shown (it can hold customer data); only type, attempts, scrubbed last error and timing.
 */
export function OutboxPanel({ canRetry }: { canRetry: boolean }) {
  const t = useTranslations("sysops.outbox");
  const tc = useTranslations("sysops.common");
  const ts = useTranslations("sysops.state");
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const problemText = useProblemMessage();
  const state = useListState<OutboxFilters>({});
  const selection = useSelection();
  const requeue = useRequeueOutbox();
  const summary = useOutboxSummary();
  const [confirm, setConfirm] = React.useState<string[] | null>(null);
  const [result, setResult] = React.useState<RequeueResultDto | null>(null);

  const f = state.filters;
  const query = useOutbox(outboxQuery({ page: state.page, pageSize: state.pageSize, search: state.search }, f));
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const pageIds = React.useMemo(() => items.filter((m) => canRequeue(m.state as OutboxState)).map((m) => m.id), [items]);
  const counts: Record<OutboxState, number> = { Pending: toNumber(summary.data?.pending), Retrying: toNumber(summary.data?.retrying), DeadLettered: toNumber(summary.data?.deadLettered) };

  const run = async (ids: string[]) => {
    try {
      const answer = await requeue.mutateAsync(ids);
      selection.clear();
      setConfirm(null);
      if (ids.length === 1) {
        setResult(null);
        toast.success(t("requeuedOne"));
      } else setResult(answer);
    } catch (failure) {
      setConfirm(null);
      toast.error(problemText(failure instanceof ApiError ? failure.problem : null));
    }
  };

  const columns = React.useMemo<ColumnDef<OutboxMessage, unknown>[]>(
    () => [
      ...(canRetry
        ? [
            {
              id: "select",
              header: () => <span className="sr-only">{t("columns.select")}</span>,
              meta: { primary: true },
              enableSorting: false,
              cell: ({ row }: { row: { original: OutboxMessage } }) =>
                canRequeue(row.original.state as OutboxState) ? (
                  <label className="-m-2 inline-flex size-11 cursor-pointer items-center justify-center">
                    <Checkbox
                      checked={selection.selected.has(row.original.id)}
                      onCheckedChange={() => selection.toggle(row.original.id)}
                      disabled={!selection.selected.has(row.original.id) && selection.selected.size >= selection.limit}
                      aria-label={t("selectRow", { type: row.original.type })}
                      data-testid="outbox-select"
                    />
                  </label>
                ) : null,
            } satisfies ColumnDef<OutboxMessage, unknown>,
          ]
        : []),
      { id: "type", header: t("columns.type"), meta: { primary: true }, enableSorting: false, cell: ({ row }) => <span className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]" data-testid="outbox-type">{row.original.type}</span> },
      { id: "state", header: t("columns.state"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <QueueStateBadge state={row.original.state as OutboxState} /> },
      { id: "attempts", header: t("columns.attempts"), meta: { align: "end", secondary: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums" data-testid="outbox-attempts">{toNumber(row.original.attempts)}</span> },
      { id: "lastError", header: t("columns.lastError"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <ErrorText value={row.original.lastError} /> },
      { id: "next", header: t("columns.next"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <span className="text-muted">{row.original.state === "DeadLettered" ? t("gaveUp", { when: dateTime(row.original.gaveUpAt) }) : dateTime(row.original.nextAttemptAt)}</span> },
      { id: "created", header: t("columns.created"), meta: { hideOnMobile: true }, enableSorting: false, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}</span> },
      ...(canRetry
        ? [
            {
              id: "actions",
              header: t("columns.actions"),
              enableSorting: false,
              meta: { align: "end", actions: true },
              cell: ({ row }: { row: { original: OutboxMessage } }) =>
                canRequeue(row.original.state as OutboxState) ? (
                  <Button variant="secondary" onClick={() => setConfirm([row.original.id])} aria-label={t("requeueFor", { type: row.original.type })} data-testid="outbox-requeue">
                    <RotateCw aria-hidden /> {t("requeue")}
                  </Button>
                ) : (
                  <span className="text-sm text-muted">{t("notRequeueable")}</span>
                ),
            } satisfies ColumnDef<OutboxMessage, unknown>,
          ]
        : []),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- selection is rebuilt on each change; the closure reads its current value
    [t, dateTime, canRetry, selection.selected, selection.limit],
  );

  const filtersActive = anyFilter(f, state.searchText);
  const bulk = confirm !== null && confirm.length > 1;

  return (
    <div className="space-y-4" data-testid="outbox-panel">
      <p className="max-w-3xl text-muted">{t("intro")}</p>

      <div role="group" aria-label={t("stateFilter")} className="flex flex-wrap gap-2" data-testid="outbox-chips">
        <Chip active={!f.state} onClick={() => state.setFilter("state", undefined)} testId="chip-all">
          {tc("all")}
        </Chip>
        {OUTBOX_STATES.map((s) => (
          <Chip key={s} active={f.state === s} onClick={() => state.setFilter("state", s)} testId={`chip-${s}`}>
            {ts(s)} <span className="font-normal tabular-nums text-muted" data-testid={`chip-count-${s}`}>{counts[s]}</span>
          </Chip>
        ))}
      </div>

      <SearchBox className="lg:w-96" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <Field label={t("type")} htmlFor="outbox-type-filter" hint={t("typeHint")}>
          <Input id="outbox-type-filter" value={f.type ?? ""} onChange={(e) => state.setFilter("type", e.target.value || undefined)} autoComplete="off" />
        </Field>
        <Field label={tc("from")} htmlFor="outbox-from">
          <Input id="outbox-from" type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor="outbox-to">
          <Input id="outbox-to" type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
      </div>
      {filtersActive ? (
        <Button variant="ghost" onClick={state.resetFilters}>
          {tc("clear")}
        </Button>
      ) : null}

      {canRetry ? <BulkBar count={selection.count} limit={selection.limit} refused={selection.refused} actionLabel={t("requeueSelected")} onAction={() => setConfirm([...selection.selected])} onSelectPage={() => selection.togglePageIds(pageIds, true)} onClear={selection.clear} pageSelectable={pageIds.length} busy={requeue.isPending && bulk} /> : null}

      {result ? <ResultNotice result={result} onDismiss={() => setResult(null)} testId="outbox-result" /> : null}
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={items}
        loading={query.isLoading}
        getRowId={(x) => x.id}
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
        loading={requeue.isPending}
        onConfirm={() => (confirm ? run(confirm) : undefined)}
      />
    </div>
  );
}

function Chip({ active, onClick, children, testId }: { active: boolean; onClick: () => void; children: React.ReactNode; testId: string }) {
  return (
    <button
      type="button"
      aria-pressed={active}
      onClick={onClick}
      data-testid={testId}
      className={cn(
        "inline-flex min-h-11 items-center gap-2 rounded-control border px-3 text-base font-semibold transition-colors",
        active ? "border-primary bg-primary-soft text-primary-dark" : "border-line bg-card text-strong [@media(hover:hover)]:hover:border-primary",
      )}
    >
      {children}
    </button>
  );
}
