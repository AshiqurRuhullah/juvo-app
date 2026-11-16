"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useFormatter, useTranslations } from "next-intl";
import { Field, Input } from "@/components/ui/form";
import { Alert, Card, CardContent, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { FilterSelect } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { useSupportReport, type KeyCount } from "./api";
import { PriorityBadge, StatusBadge, useCategoryLabel } from "./common";
import { ageParts, defaultRange, durationParts, rangeProblem, sumCounts, type TicketPriority, type TicketStatus } from "./logic";

const DailyChart = dynamic(() => import("./report-chart"), { ssr: false, loading: () => <Skeleton className="size-full" /> });

/** The support report (FR-Y05): backlog now, new and resolved per day, response and resolution times, categories, assignees, oldest open. */
export default function ReportView() {
  const t = useTranslations("support.report");
  const tc = useTranslations("support.common");
  const branches = useBranchOptions();
  const [range, setRange] = React.useState(() => defaultRange(new Date()));
  const [branchId, setBranchId] = React.useState<string | undefined>(undefined);
  const problem = rangeProblem(range.from, range.to);
  const report = useSupportReport({ from: range.from, to: range.to, branchId, enabled: problem === null });

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Field label={t("from")} htmlFor="rp-from">
          <Input id="rp-from" type="date" value={range.from} max={range.to || undefined} onChange={(e) => setRange((r) => ({ ...r, from: e.target.value }))} />
        </Field>
        <Field label={t("to")} htmlFor="rp-to">
          <Input id="rp-to" type="date" value={range.to} min={range.from || undefined} onChange={(e) => setRange((r) => ({ ...r, to: e.target.value }))} />
        </Field>
        {branches.length > 1 ? (
          <div className="self-end">
            <FilterSelect value={branchId} onChange={setBranchId} options={branches} allLabel={t("consolidated")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </div>
        ) : null}
      </div>
      {problem ? <Alert variant="warning">{t(`range.${problem}`)}</Alert> : null}
      {report.isError && !problem ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {report.isPending && !problem ? <Skeleton className="h-64" aria-busy="true" /> : null}
      {report.data ? <Body data={report.data} /> : null}
    </div>
  );
}

function Duration({ minutes }: { minutes: number | string | null | undefined }) {
  const t = useTranslations("support.report");
  const parts = durationParts(minutes === null || minutes === undefined ? null : Number(minutes));
  if (!parts) return <>{t("noData")}</>;
  const out: string[] = [];
  if (parts.days) out.push(t("d", { n: parts.days }));
  if (parts.hours) out.push(t("h", { n: parts.hours }));
  if (parts.minutes || out.length === 0) out.push(t("m", { n: parts.minutes }));
  return <>{out.join(" ")}</>;
}

function Tile({ label, value, testId }: { label: string; value: React.ReactNode; testId: string }) {
  return (
    <li className="rounded-card border border-line bg-card p-3" data-testid={testId}>
      <span className="block text-2xl font-bold tabular-nums text-strong">{value}</span>
      <span className="block text-sm text-muted">{label}</span>
    </li>
  );
}

/** A list of counts with a bar behind each name; the number is always written, the bar is only an aid. */
function Bars({ rows, label, render }: { rows: { key: string; count: number }[]; label: string; render: (key: string) => React.ReactNode }) {
  const max = Math.max(1, ...rows.map((r) => r.count));
  return (
    <ul className="space-y-2" aria-label={label}>
      {rows.map((r) => (
        <li key={r.key} className="grid grid-cols-[minmax(0,9rem)_1fr_2.5rem] items-center gap-3" data-testid={`bar-${r.key}`}>
          <span className="min-w-0 text-sm [overflow-wrap:anywhere]">{render(r.key)}</span>
          <span className="h-2 rounded-full bg-sunken" aria-hidden>
            <span className="block h-2 rounded-full bg-primary" style={{ width: `${(r.count / max) * 100}%` }} />
          </span>
          <span className="text-end font-semibold tabular-nums text-strong">{r.count}</span>
        </li>
      ))}
    </ul>
  );
}

const asRows = (rows: KeyCount[]) => rows.map((r) => ({ key: r.key, count: Number(r.count) }));

function Body({ data }: { data: NonNullable<ReturnType<typeof useSupportReport>["data"]> }) {
  const t = useTranslations("support.report");
  const format = useFormatter();
  const categoryLabel = useCategoryLabel();
  const status = React.useMemo(() => asRows(data.openByStatus), [data.openByStatus]);
  const priority = React.useMemo(() => asRows(data.openByPriority), [data.openByPriority]);
  const categories = React.useMemo(() => asRows(data.byCategory), [data.byCategory]);
  const daily = React.useMemo(
    () => data.daily.map((d) => ({ label: format.dateTime(new Date(`${d.date}T00:00:00`), { day: "numeric", month: "short" }), date: d.date, created: toNumber(d.new), resolved: toNumber(d.resolved) })),
    [data.daily, format],
  );
  const created = daily.reduce((n, d) => n + d.created, 0);
  const resolved = daily.reduce((n, d) => n + d.resolved, 0);

  return (
    <div className="space-y-4" data-testid="support-report">
      <ul className="grid grid-cols-2 gap-2 md:grid-cols-4" aria-label={t("kpis")}>
        <Tile label={t("newTickets")} value={toNumber(data.newTickets)} testId="kpi-new" />
        <Tile label={t("resolvedTickets")} value={toNumber(data.resolvedTickets)} testId="kpi-resolved" />
        <Tile label={t("openNow")} value={toNumber(data.openNow)} testId="kpi-open" />
        <Tile label={t("overdueNow")} value={toNumber(data.overdueNow)} testId="kpi-overdue" />
      </ul>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>{t("firstResponse")}</CardTitle>
          </CardHeader>
          <CardContent>
            <dl className="grid grid-cols-3 gap-3" data-testid="first-response">
              <div>
                <dt className="text-sm text-muted">{t("measured")}</dt>
                <dd className="text-lg font-semibold text-strong">{toNumber(data.firstResponse.count)}</dd>
              </div>
              <div>
                <dt className="text-sm text-muted">{t("average")}</dt>
                <dd className="text-lg font-semibold text-strong">
                  <Duration minutes={data.firstResponse.averageMinutes} />
                </dd>
              </div>
              <div>
                <dt className="text-sm text-muted">{t("median")}</dt>
                <dd className="text-lg font-semibold text-strong">
                  <Duration minutes={data.firstResponse.medianMinutes} />
                </dd>
              </div>
            </dl>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>{t("resolution")}</CardTitle>
          </CardHeader>
          <CardContent>
            <dl className="grid grid-cols-3 gap-3" data-testid="resolution">
              <div>
                <dt className="text-sm text-muted">{t("measured")}</dt>
                <dd className="text-lg font-semibold text-strong">{toNumber(data.resolution.count)}</dd>
              </div>
              <div>
                <dt className="text-sm text-muted">{t("average")}</dt>
                <dd className="text-lg font-semibold text-strong">
                  <Duration minutes={data.resolution.averageMinutes} />
                </dd>
              </div>
              <div>
                <dt className="text-sm text-muted">{t("median")}</dt>
                <dd className="text-lg font-semibold text-strong">
                  <Duration minutes={data.resolution.medianMinutes} />
                </dd>
              </div>
            </dl>
          </CardContent>
        </Card>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>{t("openByStatus")}</CardTitle>
          </CardHeader>
          <CardContent>
            <Bars rows={status} label={t("openByStatus")} render={(k) => <StatusBadge status={k as TicketStatus} />} />
            <p className="mt-2 text-sm text-muted">{t("openTotal", { count: sumCounts(status) })}</p>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>{t("openByPriority")}</CardTitle>
          </CardHeader>
          <CardContent>
            <Bars rows={priority} label={t("openByPriority")} render={(k) => <PriorityBadge priority={k as TicketPriority} />} />
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("perDay")}</CardTitle>
          <p className="text-sm text-muted" data-testid="daily-summary">
            {t("perDaySummary", { created, resolved, days: daily.length })}
          </p>
        </CardHeader>
        <CardContent>
          {daily.length > 0 ? (
            <>
              <div className="h-56" role="img" aria-label={t("perDaySummary", { created, resolved, days: daily.length })}>
                <DailyChart data={daily} createdLabel={t("newTickets")} resolvedLabel={t("resolvedTickets")} />
              </div>
              <table className="sr-only">
                <caption>{t("perDay")}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t("date")}</th>
                    <th scope="col">{t("newTickets")}</th>
                    <th scope="col">{t("resolvedTickets")}</th>
                  </tr>
                </thead>
                <tbody>
                  {daily.map((d) => (
                    <tr key={d.date}>
                      <th scope="row">{d.date}</th>
                      <td>{d.created}</td>
                      <td>{d.resolved}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          ) : (
            <p className="text-muted">{t("noData")}</p>
          )}
        </CardContent>
      </Card>

      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>{t("byCategory")}</CardTitle>
          </CardHeader>
          <CardContent>{categories.length > 0 ? <Bars rows={categories} label={t("byCategory")} render={(k) => categoryLabel(k)} /> : <p className="text-muted">{t("noData")}</p>}</CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>{t("byAssignee")}</CardTitle>
          </CardHeader>
          <CardContent>
            {data.byAssignee.length > 0 ? (
              <table className="w-full text-start text-sm" data-testid="by-assignee">
                <caption className="sr-only">{t("byAssignee")}</caption>
                <thead>
                  <tr className="text-muted">
                    <th scope="col" className="py-1 text-start font-medium">{t("assignee")}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t("total")}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t("open")}</th>
                    <th scope="col" className="py-1 text-end font-medium">{t("resolved")}</th>
                  </tr>
                </thead>
                <tbody>
                  {data.byAssignee.map((a) => (
                    <tr key={a.assigneeId ?? "none"} className="border-t border-line">
                      <th scope="row" className="py-2 text-start font-medium text-strong">{a.assigneeId ? a.name : t("unassigned")}</th>
                      <td className="py-2 text-end tabular-nums">{toNumber(a.total)}</td>
                      <td className="py-2 text-end tabular-nums">{toNumber(a.open)}</td>
                      <td className="py-2 text-end tabular-nums">{toNumber(a.resolved)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ) : (
              <p className="text-muted">{t("noData")}</p>
            )}
          </CardContent>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("oldest")}</CardTitle>
        </CardHeader>
        <CardContent>
          {data.oldestOpen.length > 0 ? (
            <ul className="divide-y divide-line" aria-label={t("oldest")} data-testid="oldest-open">
              {data.oldestOpen.map((o) => {
                const age = ageParts(Number(o.ageHours));
                return (
                  <li key={o.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
                    <Link href={`/admin/support/${o.id}`} className="inline-flex min-h-11 items-center rounded-control font-mono font-semibold text-strong hover:text-primary hover:underline">
                      {o.number}
                    </Link>
                    <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">{o.subject}</span>
                    <StatusBadge status={o.status} />
                    <PriorityBadge priority={o.priority} />
                    <span className="text-sm text-muted">{age.days > 0 ? t("ageDays", { days: age.days, hours: age.hours }) : t("ageHours", { hours: age.hours })}</span>
                  </li>
                );
              })}
            </ul>
          ) : (
            <p className="text-muted">{t("noOldest")}</p>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
