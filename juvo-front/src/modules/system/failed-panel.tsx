"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { RotateCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, Field, Input } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber } from "@/modules/catalog/format";
import { useFailedSends, useRetryFailedSends, type FailedSend, type RequeueResultDto } from "./api";
import { BulkBar } from "./bulk-bar";
import { ErrorText, ResultNotice } from "./common";
import { anyFilter, failedQuery, type FailedFilters } from "./logic";
import { useSelection } from "./use-selection";

const CHANNELS = ["Sms", "Email", "WhatsApp"] as const;

/**
 * Failed SMS, e-mail and WhatsApp sends. The recipient is masked and the text is not shown (the API does not send them with this
 * permission). Retry puts the row back to Pending with one new delivery message; text and consent decisions are not re-run.
 */
export function FailedPanel({ canRetry }: { canRetry: boolean }) {
  const t = useTranslations("sysops.failed");
  const tc = useTranslations("sysops.common");
  const tch = useTranslations("sysops.channel");
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const problemText = useProblemMessage();
  const state = useListState<FailedFilters>({});
  const selection = useSelection();
  const retry = useRetryFailedSends();
  const [confirm, setConfirm] = React.useState<string[] | null>(null);
  const [result, setResult] = React.useState<RequeueResultDto | null>(null);

  const f = state.filters;
  const query = useFailedSends(failedQuery({ page: state.page, pageSize: state.pageSize, search: state.search }, f));
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const pageIds = React.useMemo(() => items.map((m) => m.id), [items]);

  const run = async (ids: string[]) => {
    try {
      const answer = await retry.mutateAsync(ids);
      selection.clear();
      setConfirm(null);
      if (ids.length === 1) {
        setResult(null);
        toast.success(t("retriedOne"));
      } else setResult(answer);
    } catch (failure) {
      setConfirm(null);
      toast.error(problemText(failure instanceof ApiError ? failure.problem : null));
    }
  };

  const columns = React.useMemo<ColumnDef<FailedSend, unknown>[]>(
    () => [
      ...(canRetry
        ? [
            {
              id: "select",
              header: () => <span className="sr-only">{t("columns.select")}</span>,
              meta: { primary: true },
              enableSorting: false,
              cell: ({ row }: { row: { original: FailedSend } }) => (
                <label className="-m-2 inline-flex size-11 cursor-pointer items-center justify-center">
                  <Checkbox
                    checked={selection.selected.has(row.original.id)}
                    onCheckedChange={() => selection.toggle(row.original.id)}
                    disabled={!selection.selected.has(row.original.id) && selection.selected.size >= selection.limit}
                    aria-label={t("selectRow", { template: row.original.template, recipient: row.original.recipientMasked })}
                    data-testid="failed-select"
                  />
                </label>
              ),
            } satisfies ColumnDef<FailedSend, unknown>,
          ]
        : []),
      { id: "template", header: t("columns.template"), meta: { primary: true }, enableSorting: false, cell: ({ row }) => <span className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]" data-testid="failed-template">{row.original.template}</span> },
      { id: "channel", header: t("columns.channel"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <Badge variant="outline">{tch(row.original.channel as (typeof CHANNELS)[number])}</Badge> },
      { id: "recipient", header: t("columns.recipient"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <span className="font-mono text-sm" data-testid="failed-recipient">{row.original.recipientMasked}</span> },
      { id: "error", header: t("columns.error"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <ErrorText value={row.original.lastError} /> },
      { id: "attempts", header: t("columns.attempts"), meta: { align: "end", secondary: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.attempts)}</span> },
      { id: "queued", header: t("columns.queued"), meta: { hideOnMobile: true }, enableSorting: false, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.queuedAt)}</span> },
      ...(canRetry
        ? [
            {
              id: "actions",
              header: t("columns.actions"),
              enableSorting: false,
              meta: { align: "end", actions: true },
              cell: ({ row }: { row: { original: FailedSend } }) => (
                <Button variant="secondary" onClick={() => setConfirm([row.original.id])} aria-label={t("retryFor", { template: row.original.template, recipient: row.original.recipientMasked })} data-testid="failed-retry">
                  <RotateCw aria-hidden /> {t("retry")}
                </Button>
              ),
            } satisfies ColumnDef<FailedSend, unknown>,
          ]
        : []),
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- selection is rebuilt on each change; the closure reads its current value
    [t, tch, dateTime, canRetry, selection.selected, selection.limit],
  );

  const filtersActive = anyFilter(f, state.searchText);
  const bulk = confirm !== null && confirm.length > 1;

  return (
    <div className="space-y-4" data-testid="failed-panel">
      <p className="max-w-3xl text-muted">{t("intro")}</p>
      <SearchBox className="lg:w-96" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <FilterSelect value={f.channel} onChange={(v) => state.setFilter("channel", v)} options={CHANNELS.map((c) => ({ value: c, label: tch(c) }))} allLabel={t("anyChannel")} label={t("columns.channel")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <Field label={t("template")} htmlFor="failed-template-filter">
          <Input id="failed-template-filter" value={f.template ?? ""} onChange={(e) => state.setFilter("template", e.target.value || undefined)} autoComplete="off" placeholder="order." />
        </Field>
        <Field label={tc("from")} htmlFor="failed-from">
          <Input id="failed-from" type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor="failed-to">
          <Input id="failed-to" type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
      </div>
      {filtersActive ? (
        <Button variant="ghost" onClick={state.resetFilters}>
          {tc("clear")}
        </Button>
      ) : null}

      {canRetry ? <BulkBar count={selection.count} limit={selection.limit} refused={selection.refused} actionLabel={t("retrySelected")} onAction={() => setConfirm([...selection.selected])} onSelectPage={() => selection.togglePageIds(pageIds, true)} onClear={selection.clear} pageSelectable={pageIds.length} busy={retry.isPending && bulk} /> : null}

      {result ? <ResultNotice result={result} onDismiss={() => setResult(null)} testId="failed-result" /> : null}
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
        loading={retry.isPending}
        onConfirm={() => (confirm ? run(confirm) : undefined)}
      />
    </div>
  );
}
