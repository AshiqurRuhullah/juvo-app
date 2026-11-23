"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import type { UseQueryResult } from "@tanstack/react-query";
import { CheckCircle2, OctagonX } from "lucide-react";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { useDateTime, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import type { SystemStatus } from "./api";
import { ErrorText } from "./common";
import { formatMs, formatSpan } from "./logic";

type Route = SystemStatus["slowestRoutes"][number];

/**
 * The system status panel (`GET /system/status`): database, version, uptime, queue backlog, failed jobs, slowest routes and recent
 * unhandled errors. The numbers come from this server process only and start again at zero after a restart; the panel says so.
 */
export function StatusPanel({ query }: { query: UseQueryResult<SystemStatus> }) {
  const t = useTranslations("sysops.status2");
  const tu = useTranslations("sysops.units");
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const units = { d: tu("d"), h: tu("h"), min: tu("min"), s: tu("s") };
  const routes = React.useMemo(() => query.data?.slowestRoutes ?? [], [query.data]);

  const columns = React.useMemo<ColumnDef<Route, unknown>[]>(
    () => [
      { id: "route", header: t("routes.route"), meta: { primary: true }, enableSorting: false, cell: ({ row }) => <span className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]" data-testid="route-name"><span className="text-muted">{row.original.method}</span> {row.original.route}</span> },
      { id: "count", header: t("routes.count"), meta: { align: "end", secondary: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.count)}</span> },
      { id: "avg", header: t("routes.average"), meta: { align: "end", secondary: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums">{formatMs(row.original.averageMs, tu("s"))}</span> },
      { id: "max", header: t("routes.max"), meta: { align: "end", secondary: true }, enableSorting: false, cell: ({ row }) => <span className="tabular-nums">{formatMs(row.original.maxMs, tu("s"))}</span> },
    ],
    [t, tu],
  );

  return (
    <section aria-labelledby="status-title" data-testid="status-panel">
      <h2 id="status-title" className="font-display text-xl font-semibold text-strong">
        {t("title")}
      </h2>
      <Alert variant="info" className="mt-3" data-testid="status-scope-note">
        {t("scopeNote")}
      </Alert>

      <div className="mt-4 space-y-6">
        {query.isPending ? (
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4" aria-busy="true">
            <Skeleton className="h-24" />
            <Skeleton className="h-24" />
            <Skeleton className="h-24" />
            <Skeleton className="h-24" />
          </div>
        ) : query.isError ? (
          <Alert variant="danger">{t("loadFailed")}</Alert>
        ) : (
          <>
            <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <Stat label={t("database")} testId="stat-database">
                {query.data.databaseReachable ? (
                  <Badge variant="success">
                    <CheckCircle2 className="size-3.5" aria-hidden />
                    {t("reachable")}
                  </Badge>
                ) : (
                  <Badge variant="danger">
                    <OctagonX className="size-3.5" aria-hidden />
                    {t("unreachable")}
                  </Badge>
                )}
              </Stat>
              <Stat label={t("version")} testId="stat-version">
                <span className="font-mono text-sm">{query.data.version}</span>
              </Stat>
              <Stat label={t("uptime")} testId="stat-uptime">
                {formatSpan(query.data.uptimeSeconds, units)}
                <span className="block text-sm font-normal text-muted">{t("since", { when: dateTime(query.data.startedAt) })}</span>
              </Stat>
              <Stat label={t("failedJobs")} testId="stat-failed-jobs">
                <span className="tabular-nums">{toNumber(query.data.failedJobs24h)}</span>
              </Stat>
              <Stat label={t("backlog")} testId="stat-backlog" className="sm:col-span-2">
                <span className="text-base font-normal">
                  {t("backlogCounts", { pending: toNumber(query.data.outbox.pending), retrying: toNumber(query.data.outbox.retrying), dead: toNumber(query.data.outbox.deadLettered) })}
                </span>
                <span className="block text-sm font-normal text-muted" data-testid="stat-oldest">
                  {query.data.outbox.oldestOpenAgeSeconds === null || query.data.outbox.oldestOpenAgeSeconds === undefined ? t("noOpen") : t("oldest", { age: formatSpan(query.data.outbox.oldestOpenAgeSeconds, units) })}
                </span>
              </Stat>
              <Stat label={t("slowRequests")} testId="stat-slow" className="sm:col-span-2">
                <span className="tabular-nums">{toNumber(query.data.slowRequests)}</span>
                <span className="block text-sm font-normal text-muted">{t("slowDetail", { threshold: formatMs(query.data.slowThresholdMs, tu("s")), seen: toNumber(query.data.requestsSeen), kept: toNumber(query.data.requestsKept) })}</span>
              </Stat>
            </dl>

            <div>
              <h3 className="mb-2 text-lg font-semibold text-strong">{t("routes.title")}</h3>
              <DataTable
                caption={t("routes.title")}
                columns={columns}
                data={routes}
                getRowId={(x) => `${x.method} ${x.route}`}
                labels={{ ...labels, empty: t("routes.empty"), emptyDescription: t("routes.emptyDescription") }}
              />
            </div>

            <div>
              <h3 className="mb-2 text-lg font-semibold text-strong">{t("errors.title")}</h3>
              {query.data.recentErrors.length === 0 ? (
                <p className="rounded-card border border-dashed border-line p-6 text-center text-muted" data-testid="errors-empty">
                  {t("errors.empty")}
                </p>
              ) : (
                <ul className="divide-y divide-line rounded-card border border-line bg-card" data-testid="errors-list">
                  {query.data.recentErrors.map((e) => (
                    <li key={`${e.correlationId}-${e.at}`} className="space-y-1 p-4" data-testid="error-row">
                      <p className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]">{e.exceptionType}</p>
                      <p className="text-sm text-muted [overflow-wrap:anywhere]">
                        {e.route} · {dateTime(e.at, "long")}
                      </p>
                      <ErrorText value={e.message} />
                      <p className="text-sm text-muted [overflow-wrap:anywhere]">
                        {t("errors.correlation")}: <span className="font-mono text-xs text-strong">{e.correlationId}</span>
                      </p>
                    </li>
                  ))}
                </ul>
              )}
            </div>
          </>
        )}
      </div>
    </section>
  );
}

function Stat({ label, children, className, testId }: { label: string; children: React.ReactNode; className?: string; testId: string }) {
  return (
    <div className={`rounded-card border border-line bg-card p-4 shadow-card ${className ?? ""}`} data-testid={testId}>
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="mt-1 text-xl font-semibold text-strong [overflow-wrap:anywhere]">{children}</dd>
    </div>
  );
}
