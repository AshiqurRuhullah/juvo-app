"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Info, RotateCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber } from "@/modules/catalog/format";
import { dayRange } from "@/modules/orders/status";
import { LOG_CHANNELS, LOG_STATUSES, useNotificationLogs, useResendLog, type NotificationLog, type NotificationStatus } from "./api";

type Filters = { status?: string; channel?: string; template?: string; recipient?: string; from?: string; to?: string };

const tone = (status: NotificationStatus) => (status === "Sent" ? "success" : status === "Pending" ? "info" : status === "Skipped" ? "neutral" : "danger");
export const canResend = (status: NotificationStatus) => status === "Failed" || status === "DeadLetter";

export function LogStatusBadge({ status }: { status: NotificationStatus }) {
  const t = useTranslations("notifications.status");
  return <Badge variant={tone(status)}>{t(status)}</Badge>;
}

/** The delivery log: filters, a detail dialog with the full text and the provider's answer, and resend for failed messages. */
export function LogsPanel({ canRetry }: { canRetry: boolean }) {
  const t = useTranslations("notifications.logs");
  const tc = useTranslations("notifications.common");
  const tch = useTranslations("notifications.channel");
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const problemText = useProblemMessage();
  const state = useListState<Filters>({}, 20);
  const resend = useResendLog();
  const [detail, setDetail] = React.useState<NotificationLog | null>(null);
  const ts = useTranslations("notifications.status");

  const f = state.filters;
  const range = dayRange(f.from, f.to);
  const query = useNotificationLogs({ page: state.page, pageSize: state.pageSize, sort: state.sort, search: state.search, status: f.status, channel: f.channel, template: f.template, recipient: f.recipient, ...range });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);

  const doResend = async (log: NotificationLog) => {
    try {
      const answer = await resend.mutateAsync(log.id);
      toast.success(t("resent"));
      setDetail((current) => (current && current.id === log.id ? answer : current));
    } catch (failure) {
      toast.error(problemText(failure instanceof ApiError ? failure.problem : null));
    }
  };

  const columns = React.useMemo<ColumnDef<NotificationLog, unknown>[]>(
    () => [
      { id: "template", header: t("columns.template"), meta: { primary: true }, enableSorting: false, cell: ({ row }) => <span className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]" data-testid="log-template">{row.original.template}</span> },
      { id: "recipient", header: t("columns.recipient"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <span className="text-strong [overflow-wrap:anywhere]">{row.original.recipient}</span> },
      { id: "channel", header: t("columns.channel"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <Badge variant="outline">{tch(row.original.channel)}</Badge> },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <LogStatusBadge status={row.original.status} /> },
      { id: "attempts", header: t("columns.attempts"), meta: { align: "end", hideOnMobile: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.attempts)}</span> },
      { id: "queuedAt", header: t("columns.queued"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.queuedAt)}</span> },
      {
        id: "actions",
        header: t("columns.actions"),
        enableSorting: false,
        meta: { align: "end" },
        cell: ({ row }) => (
          <span className="flex flex-wrap justify-end gap-2">
            <Button variant="secondary" onClick={() => setDetail(row.original)} aria-label={t("detailsFor", { template: row.original.template, recipient: row.original.recipient })}>
              <Info aria-hidden /> {t("details")}
            </Button>
            {canRetry && canResend(row.original.status) ? (
              <Button variant="secondary" loading={resend.isPending && resend.variables === row.original.id} onClick={() => void doResend(row.original)} data-testid="log-resend">
                <RotateCw aria-hidden /> {t("resend")}
              </Button>
            ) : null}
          </span>
        ),
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- doResend only closes over stable mutation helpers
    [t, tch, dateTime, canRetry, resend.isPending, resend.variables],
  );

  const filtersActive = !!(state.searchText || Object.values(f).some(Boolean));

  return (
    <div className="space-y-4">
      <SearchBox className="lg:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <FilterSelect value={f.status} onChange={(v) => state.setFilter("status", v)} options={LOG_STATUSES.map((s) => ({ value: s, label: ts(s) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.channel} onChange={(v) => state.setFilter("channel", v)} options={LOG_CHANNELS.map((c) => ({ value: c, label: tch(c) }))} allLabel={t("anyChannel")} label={t("columns.channel")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <Field label={t("template")} htmlFor="log-template">
          <Input id="log-template" value={f.template ?? ""} onChange={(e) => state.setFilter("template", e.target.value || undefined)} placeholder="order.placed" autoComplete="off" />
        </Field>
        <Field label={t("recipient")} htmlFor="log-recipient">
          <Input id="log-recipient" value={f.recipient ?? ""} onChange={(e) => state.setFilter("recipient", e.target.value || undefined)} autoComplete="off" />
        </Field>
        <Field label={t("from")} htmlFor="log-from">
          <Input id="log-from" type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="log-to">
          <Input id="log-to" type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
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

      <Dialog open={!!detail} onOpenChange={(open) => (open ? undefined : setDetail(null))}>
        <ModalContent size="md" closeLabel={tc("close")}>
          {detail ? (
            <div className="space-y-4" data-testid="log-detail">
              <DialogTitle className="font-display text-xl font-semibold text-strong">{t("detailTitle")}</DialogTitle>
              <DialogDescription className="text-muted">
                <span className="font-mono">{detail.template}</span> · {tch(detail.channel)} · {detail.recipient}
              </DialogDescription>
              <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
                <dt className="text-muted">{t("columns.status")}</dt>
                <dd>
                  <LogStatusBadge status={detail.status} />
                </dd>
                <dt className="text-muted">{t("columns.attempts")}</dt>
                <dd className="text-strong">{toNumber(detail.attempts)}</dd>
                <dt className="text-muted">{t("columns.queued")}</dt>
                <dd className="text-strong">{dateTime(detail.queuedAt, "long")}</dd>
                <dt className="text-muted">{t("sentAt")}</dt>
                <dd className="text-strong">{detail.status === "Sent" ? dateTime(detail.sentAt, "long") : "-"}</dd>
                <dt className="text-muted">{t("version")}</dt>
                <dd className="text-strong">
                  {detail.language ?? "-"} · v{toNumber(detail.templateVersion)}
                </dd>
              </dl>
              {detail.subject ? <p className="font-semibold text-strong [overflow-wrap:anywhere]">{detail.subject}</p> : null}
              <p className="whitespace-pre-wrap rounded-control bg-sunken p-3 text-strong [overflow-wrap:anywhere]" data-testid="log-body">
                {detail.body}
              </p>
              {detail.lastError ? (
                <Alert variant="danger" title={t("lastError")}>
                  {detail.lastError}
                </Alert>
              ) : null}
              {detail.providerResponse ? (
                <div>
                  <p className="text-sm font-semibold text-strong">{t("providerResponse")}</p>
                  <p className="whitespace-pre-wrap font-mono text-sm text-muted [overflow-wrap:anywhere]">{detail.providerResponse}</p>
                </div>
              ) : null}
              <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
                <Button variant="secondary" onClick={() => setDetail(null)}>
                  {tc("close")}
                </Button>
                {canRetry && canResend(detail.status) ? (
                  <Button loading={resend.isPending} onClick={() => void doResend(detail)} data-testid="log-detail-resend">
                    <RotateCw aria-hidden /> {t("resend")}
                  </Button>
                ) : null}
              </div>
            </div>
          ) : null}
        </ModalContent>
      </Dialog>
    </div>
  );
}
