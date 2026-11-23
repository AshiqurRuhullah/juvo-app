"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Info } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useJobRuns, useJobSummary, type JobRun } from "./api";
import { ErrorText, Fact, RunOutcomeBadge } from "./common";
import { JOB_OUTCOMES, anyFilter, formatMs, jobNames, runQuery, type JobOutcome, type RunFilters } from "./logic";

/** Job runs: every attempt of every background job, newest first, with filters and a detail dialog (attempt, correlation id, scrubbed error). */
export function RunsPanel() {
  const t = useTranslations("sysops.runs");
  const tc = useTranslations("sysops.common");
  const to = useTranslations("sysops.outcome");
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const state = useListState<RunFilters>({});
  const summary = useJobSummary();
  const [detail, setDetail] = React.useState<JobRun | null>(null);

  const f = state.filters;
  const query = useJobRuns(runQuery({ page: state.page, pageSize: state.pageSize, search: state.search }, f));
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const names = React.useMemo(() => jobNames(summary.data ?? []), [summary.data]);

  const columns = React.useMemo<ColumnDef<JobRun, unknown>[]>(
    () => [
      { id: "job", header: t("columns.job"), meta: { primary: true }, enableSorting: false, cell: ({ row }) => <span className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]" data-testid="run-job">{row.original.jobName}</span> },
      { id: "outcome", header: t("columns.outcome"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <RunOutcomeBadge outcome={row.original.outcome as JobOutcome} /> },
      { id: "started", header: t("columns.started"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.startedAt)}</span> },
      { id: "duration", header: t("columns.duration"), meta: { align: "end", hideOnMobile: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums">{row.original.durationMs === null || row.original.durationMs === undefined ? "-" : formatMs(row.original.durationMs)}</span> },
      { id: "attempt", header: t("columns.attempt"), meta: { align: "end", hideOnMobile: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.attempt)}</span> },
      { id: "reference", header: t("columns.reference"), meta: { hideOnMobile: true }, enableSorting: false, cell: ({ row }) => <span className="font-mono text-xs text-muted [overflow-wrap:anywhere]">{row.original.reference ?? "-"}</span> },
      {
        id: "actions",
        header: t("columns.actions"),
        enableSorting: false,
        meta: { align: "end", actions: true },
        cell: ({ row }) => (
          <Button variant="secondary" onClick={() => setDetail(row.original)} aria-label={t("detailsFor", { job: row.original.jobName })} data-testid="run-details">
            <Info aria-hidden /> {t("details")}
          </Button>
        ),
      },
    ],
    [t, dateTime],
  );

  return (
    <div className="space-y-4" data-testid="runs-panel">
      <SearchBox className="lg:w-96" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
        <FilterSelect value={f.job} onChange={(v) => state.setFilter("job", v)} options={names.map((n) => ({ value: n, label: n }))} allLabel={t("anyJob")} label={t("columns.job")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={f.outcome} onChange={(v) => state.setFilter("outcome", v)} options={JOB_OUTCOMES.map((o) => ({ value: o, label: to(o) }))} allLabel={t("anyOutcome")} label={t("columns.outcome")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <Field label={t("reference")} htmlFor="run-reference">
          <Input id="run-reference" value={f.reference ?? ""} onChange={(e) => state.setFilter("reference", e.target.value || undefined)} autoComplete="off" />
        </Field>
        <Field label={tc("from")} htmlFor="run-from">
          <Input id="run-from" type="date" value={f.from ?? ""} max={f.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor="run-to">
          <Input id="run-to" type="date" value={f.to ?? ""} min={f.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
      </div>
      {anyFilter(f, state.searchText) ? (
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
        labels={{ ...labels, empty: anyFilter(f, state.searchText) ? labels.empty : t("empty"), emptyDescription: anyFilter(f, state.searchText) ? labels.emptyDescription : t("emptyDescription") }}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
      />

      <Dialog open={!!detail} onOpenChange={(open) => (open ? undefined : setDetail(null))}>
        <ModalContent size="md" closeLabel={tc("close")}>
          {detail ? (
            <div className="space-y-4" data-testid="run-detail">
              <DialogTitle className="font-display text-xl font-semibold text-strong">{t("detailTitle")}</DialogTitle>
              <DialogDescription className="font-mono text-sm text-muted [overflow-wrap:anywhere]">{detail.jobName}</DialogDescription>
              <dl className="grid gap-3 sm:grid-cols-2">
                <Fact label={t("columns.outcome")}>
                  <RunOutcomeBadge outcome={detail.outcome as JobOutcome} />
                </Fact>
                <Fact label={t("columns.attempt")}>{toNumber(detail.attempt)}</Fact>
                <Fact label={t("columns.started")}>{dateTime(detail.startedAt, "long")}</Fact>
                <Fact label={t("finished")}>{dateTime(detail.finishedAt, "long")}</Fact>
                <Fact label={t("columns.duration")}>{detail.durationMs === null || detail.durationMs === undefined ? "-" : formatMs(detail.durationMs)}</Fact>
                <Fact label={t("columns.reference")}>
                  <span className="font-mono text-xs">{detail.reference ?? "-"}</span>
                </Fact>
                <Fact label={t("correlation")} className="sm:col-span-2" testId="run-correlation">
                  <span className="font-mono text-xs">{detail.correlationId ?? "-"}</span>
                </Fact>
              </dl>
              {detail.error ? (
                <Alert variant="danger" title={t("error")}>
                  <ErrorText value={detail.error} />
                </Alert>
              ) : null}
              <div className="flex justify-end">
                <Button variant="secondary" onClick={() => setDetail(null)}>
                  {tc("close")}
                </Button>
              </div>
            </div>
          ) : null}
        </ModalContent>
      </Dialog>
    </div>
  );
}
