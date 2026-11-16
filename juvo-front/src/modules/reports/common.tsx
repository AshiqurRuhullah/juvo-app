"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { EyeOff, Info } from "lucide-react";
import type { UseQueryResult } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Select, type SelectOption } from "@/components/ui/select";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { usePermissions, useSession } from "@/components/providers/session-provider";
import { useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { cn } from "@/lib/utils";
import { useDateOnly, useMoney2 } from "@/modules/purchasing/common";
import { useBranchOptions } from "@/modules/inventory/common";
import {
  GROUP_BY,
  bangladeshToday,
  detectPreset,
  durationParts,
  effectiveGroupBy,
  exportPermission,
  filtersToSearch,
  isIsoDate,
  num,
  parseFilters,
  percentText,
  presetRange,
  rangeLimit,
  rangeProblem,
  PRESETS,
  type GroupByReport,
  type RangeProblem,
  type ReportFilters,
  type ReportName,
} from "./logic";
import type { Series } from "./charts";

// Recharts and the export menu load only when a report needs them.
export const TrendChart = dynamic(() => import("./charts").then((m) => m.TrendChart), { ssr: false, loading: () => <Skeleton className="size-full" /> });
export const BarsChart = dynamic(() => import("./charts").then((m) => m.BarsChart), { ssr: false, loading: () => <Skeleton className="size-full" /> });
const ExportMenu = dynamic(() => import("./export-menu").then((m) => m.ExportMenu), { ssr: false, loading: () => <Skeleton className="h-11 w-28" /> });

/* ---- Formatting ------------------------------------------------------------------------------------------------------------- */

export function useReportFormat() {
  const format = useFormatter();
  const locale = useLocale();
  const t = useTranslations("reports.duration");
  const money = useMoney2();
  const dateOnly = useDateOnly();
  return React.useMemo(
    () => ({
      money,
      dateOnly,
      int: (v: number | string | null | undefined) => format.number(num(v), { maximumFractionDigits: 0 }),
      pct: (v: number | string | null | undefined) => percentText(v, locale),
      /** Money or the word "hidden" for a cost figure the person may not see (never 0). */
      cost: (v: number | string | null | undefined) => (v === null || v === undefined ? "-" : money(v)),
      duration: (hours: number | string | null | undefined) => {
        const p = durationParts(hours);
        if (!p) return "-";
        if (p.days) return p.hours ? t("dh", { d: format.number(p.days), h: format.number(p.hours) }) : t("d", { d: format.number(p.days) });
        if (p.hours) return p.minutes ? t("hm", { h: format.number(p.hours), m: format.number(p.minutes) }) : t("h", { h: format.number(p.hours) });
        return t("m", { m: format.number(p.minutes) });
      },
    }),
    [money, dateOnly, format, locale, t],
  );
}

/* ---- Filters in the address bar --------------------------------------------------------------------------------------------- */

export type ReportContext = {
  filters: ReportFilters;
  today: string;
  kind: GroupByReport | "plain";
  problem: RangeProblem | null;
  /** The report may be requested (the range is valid). */
  ready: boolean;
  change: (patch: Partial<ReportFilters>) => void;
};

/** The filters of a report, read from and written to the address, so a report can be bookmarked and shared. */
export function useReportFilters(kind: GroupByReport | "plain", maxDays?: number): ReportContext {
  const params = useSearchParams();
  const router = useRouter();
  const pathname = usePathname();
  const { me } = useSession();
  const today = React.useMemo(() => bangladeshToday(), []);
  const defaultBranch = !me.isAllBranches && me.activeBranchId ? me.activeBranchId : undefined;
  const filters = React.useMemo(() => parseFilters(params, kind, today, defaultBranch), [params, kind, today, defaultBranch]);
  const problem = rangeProblem(filters.from, filters.to, effectiveGroupBy(filters), maxDays);
  const change = React.useCallback(
    (patch: Partial<ReportFilters>) => router.replace(`${pathname}?${filtersToSearch({ ...filters, ...patch })}`, { scroll: false }),
    [router, pathname, filters],
  );
  return { filters, today, kind, problem, ready: problem === null, change };
}

export function RangeAlert({ problem, groupBy, max }: { problem: RangeProblem; groupBy?: string; max?: number }) {
  const t = useTranslations("reports.filters.range");
  const ta = useTranslations("errors");
  const text = problem === "missing" ? t("missing") : problem === "invalid" ? ta("REPORT_RANGE_INVALID") : `${ta("REPORT_RANGE_TOO_LARGE")} ${t("limit", { days: rangeLimit(groupBy, max) })}`;
  return (
    <Alert variant="danger" data-testid="range-problem">
      {text}
    </Alert>
  );
}

function DateRange({ ctx, maxDays, fromRef }: { ctx: ReportContext; maxDays?: number; fromRef: React.RefObject<HTMLInputElement | null> }) {
  const t = useTranslations("reports.filters");
  const [draft, setDraft] = React.useState({ from: ctx.filters.from, to: ctx.filters.to });
  const group = effectiveGroupBy(ctx.filters);
  const problem = isIsoDate(draft.from) && isIsoDate(draft.to) ? rangeProblem(draft.from, draft.to, group, maxDays) : "missing";
  const edit = (next: { from: string; to: string }) => {
    setDraft(next);
    if (isIsoDate(next.from) && isIsoDate(next.to) && rangeProblem(next.from, next.to, group, maxDays) === null) ctx.change({ from: next.from, to: next.to });
  };
  return (
    <>
      <Field label={t("from")} htmlFor="rf-from">
        <Input id="rf-from" ref={fromRef} type="date" value={draft.from} max={draft.to || undefined} onChange={(e) => edit({ ...draft, from: e.target.value })} />
      </Field>
      <Field label={t("to")} htmlFor="rf-to">
        <Input id="rf-to" type="date" value={draft.to} min={draft.from || undefined} onChange={(e) => edit({ ...draft, to: e.target.value })} />
      </Field>
      {problem && (draft.from !== ctx.filters.from || draft.to !== ctx.filters.to) ? (
        <div className="sm:col-span-2 xl:col-span-4">
          <RangeAlert problem={problem} groupBy={group} max={maxDays} />
        </div>
      ) : null}
    </>
  );
}

/** Branch choices: one branch, or everything the person may see (consolidated for people who can pick every branch). */
export function useBranchChoices() {
  const { me } = useSession();
  const { can } = usePermissions();
  const options = useBranchOptions();
  const t = useTranslations("reports.filters");
  const canPickAll = !!me.staff?.isGlobalScope || can("ums.branch.switch-all", "anywhere");
  return React.useMemo<{ options: SelectOption[]; canPickAll: boolean }>(() => ({ options: [{ value: "", label: canPickAll ? t("allBranches") : t("allMine") }, ...options], canPickAll }), [options, canPickAll, t]);
}

export function FilterBar({
  ctx,
  groupLabel,
  groupOptions,
  groupDefault,
  showDays,
  maxDays,
  hideBranch,
}: {
  ctx: ReportContext;
  groupLabel?: string;
  groupOptions?: SelectOption[];
  groupDefault?: string;
  showDays?: boolean;
  maxDays?: number;
  hideBranch?: boolean;
}) {
  const t = useTranslations("reports.filters");
  const tc = useTranslations("accounting.common");
  const fromRef = React.useRef<HTMLInputElement>(null);
  const branches = useBranchChoices();
  const { filters } = ctx;
  const preset = detectPreset(filters.from, filters.to, ctx.today);
  const [days, setDays] = React.useState(String(filters.days ?? 60));

  return (
    <Card className="p-4" data-testid="report-filters">
      <div role="group" aria-label={t("presets")} className="flex flex-wrap gap-2">
        {PRESETS.map((p) => (
          <Button key={p} variant={preset === p ? "primary" : "secondary"} aria-pressed={preset === p} data-testid={`preset-${p}`} className="min-w-0" onClick={() => ctx.change(presetRange(p, ctx.today))}>
            {t(`presetNames.${p}`)}
          </Button>
        ))}
        <Button variant={preset === "custom" ? "primary" : "secondary"} aria-pressed={preset === "custom"} data-testid="preset-custom" onClick={() => fromRef.current?.focus()}>
          {t("presetNames.custom")}
        </Button>
      </div>
      <div className="mt-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <DateRange key={`${filters.from}|${filters.to}`} ctx={ctx} maxDays={maxDays} fromRef={fromRef} />
        {hideBranch ? null : (
          <Field label={t("branch")} htmlFor="rf-branch">
            <Select id="rf-branch" value={filters.branchId ?? ""} onValueChange={(v) => ctx.change({ branchId: v || undefined })} options={branches.options} placeholder={t("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        )}
        {groupOptions ? (
          <Field label={groupLabel ?? t("groupBy")} htmlFor="rf-group">
            <Select id="rf-group" value={filters.groupBy ?? groupDefault ?? groupOptions[0]?.value} onValueChange={(v) => ctx.change({ groupBy: v === (groupDefault ?? groupOptions[0]?.value) ? undefined : v })} options={groupOptions} placeholder={t("groupBy")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        ) : null}
        {showDays ? (
          <Field label={t("days")} htmlFor="rf-days" hint={t("daysHint")}>
            <Input
              id="rf-days"
              type="number"
              inputMode="numeric"
              min={1}
              max={3650}
              value={days}
              onChange={(e) => setDays(e.target.value)}
              onBlur={() => {
                const n = Number(days);
                if (Number.isInteger(n) && n >= 1 && n <= 3650) ctx.change({ days: n === 60 ? undefined : n });
                else setDays(String(filters.days ?? 60));
              }}
              onKeyDown={(e) => e.key === "Enter" && e.currentTarget.blur()}
            />
          </Field>
        ) : null}
      </div>
    </Card>
  );
}

export function useGroupOptions(report: "sales" | "finance" | "pnl"): SelectOption[] {
  const t = useTranslations("reports.groupBy");
  return React.useMemo(() => GROUP_BY[report].map((g) => ({ value: g, label: t(g) })), [report, t]);
}

/* ---- Frame, notices, tiles, tables ------------------------------------------------------------------------------------------ */

export type Flags = { costHidden?: boolean; gatewayFeesRecorded?: boolean; truncated?: boolean; costMissingLines?: number };

/** The API's honest notices: hidden cost, fees that are not recorded, cut lists, lines without a cost, and its own notes. */
export function Notices({ notes, flags }: { notes?: string[]; flags?: Flags }) {
  const t = useTranslations("reports.notices");
  return (
    <div className="space-y-3" data-testid="report-notices">
      {flags?.costHidden ? (
        <Alert variant="warning" data-testid="notice-cost-hidden">
          <span className="flex items-start gap-2">
            <EyeOff className="mt-0.5 size-4 shrink-0" aria-hidden />
            {t("costHidden")}
          </span>
        </Alert>
      ) : null}
      {flags?.gatewayFeesRecorded === false ? <Alert variant="info" data-testid="notice-gateway-fees">{t("gatewayFees")}</Alert> : null}
      {flags?.truncated ? <Alert variant="warning" data-testid="notice-truncated">{t("truncated")}</Alert> : null}
      {flags?.costMissingLines ? <Alert variant="warning" data-testid="notice-cost-missing">{t("costMissing", { count: flags.costMissingLines })}</Alert> : null}
      {notes && notes.length > 0 ? (
        <details className="rounded-control border border-line bg-card p-3 text-sm" data-testid="report-notes">
          <summary className="flex min-h-11 cursor-pointer items-center gap-2 font-medium text-strong">
            <Info className="size-4 text-muted" aria-hidden /> {t("about")}
          </summary>
          <ul className="mt-2 list-disc space-y-1 ps-5 text-muted">
            {notes.map((n) => (
              <li key={n}>{n}</li>
            ))}
          </ul>
        </details>
      ) : null}
    </div>
  );
}

export type TileData = { key: string; label: string; value: React.ReactNode; hint?: string; emphasis?: boolean };

export function Tiles({ tiles, testId = "report-tiles" }: { tiles: TileData[]; testId?: string }) {
  return (
    <dl className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-4" data-testid={testId}>
      {tiles.map((x) => (
        <div key={x.key} className="min-w-0 rounded-card border border-line bg-card p-4 shadow-card">
          <dt className="text-sm text-muted">{x.label}</dt>
          <dd className={cn("mt-1 font-semibold tabular-nums text-strong [overflow-wrap:anywhere]", x.emphasis ? "text-2xl" : "text-xl")} data-testid={`tile-${x.key}`}>
            {x.value}
          </dd>
          {x.hint ? <dd className="mt-1 text-xs text-muted">{x.hint}</dd> : null}
        </div>
      ))}
    </dl>
  );
}

/** A card holding a chart: the visible title, a text summary the screen reader gets, and a visually hidden data table. */
export function ChartCard({
  title,
  description,
  summary,
  legend,
  table,
  children,
  height = "h-64 sm:h-72",
}: {
  title: string;
  description?: string;
  summary: string;
  legend: Series[];
  table: { caption: string; headers: string[]; rows: (string | number)[][] };
  children: React.ReactNode;
  height?: string;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        {description ? <CardDescription>{description}</CardDescription> : null}
      </CardHeader>
      <CardContent>
        <ul className="mb-3 flex flex-wrap gap-x-5 gap-y-1 text-sm text-muted">
          {legend.map((s) => (
            <li key={s.key} className="flex items-center gap-2">
              <span className={cn("w-5", s.dashed ? "border-t-2 border-dashed" : "h-1 rounded-full")} style={s.dashed ? { borderColor: s.color } : { backgroundColor: s.color }} aria-hidden />
              {s.label}
            </li>
          ))}
        </ul>
        <div role="img" aria-label={summary} className={cn("w-full", height)} data-testid="chart">
          {children}
        </div>
        <p className="mt-2 text-sm text-muted" data-testid="chart-summary">
          {summary}
        </p>
        <div className="sr-only">
        <table>
          <caption>{table.caption}</caption>
          <thead>
            <tr>
              {table.headers.map((h) => (
                <th key={h} scope="col">
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.map((r, i) => (
              <tr key={i}>
                {r.map((c, j) => (j === 0 ? <th key={j} scope="row">{c}</th> : <td key={j}>{c}</td>))}
              </tr>
            ))}
          </tbody>
        </table>
        </div>
      </CardContent>
    </Card>
  );
}

/** A section card: title, optional note, then the content (usually a table). */
export function Section({ title, description, children, testId }: { title: string; description?: string; children: React.ReactNode; testId?: string }) {
  return (
    <Card data-testid={testId}>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        {description ? <CardDescription>{description}</CardDescription> : null}
      </CardHeader>
      <CardContent>{children}</CardContent>
    </Card>
  );
}

/** A table with server rows only: numeric columns right aligned with tabular figures (`numeric: true`), cards on phones. */
export function ReportTable<T>({ caption, columns, rows, getRowId, loading }: { caption: string; columns: ColumnDef<T, unknown>[]; rows: T[]; getRowId: (row: T, index: number) => string; loading?: boolean }) {
  const labels = useTableLabels();
  const index = React.useMemo(() => new Map(rows.map((r, i) => [r, i])), [rows]);
  return <DataTable caption={caption} columns={columns} data={rows} loading={loading} getRowId={(r) => getRowId(r, index.get(r) ?? 0)} labels={labels} />;
}

/** Loading skeletons while the first answer is on its way. */
export function ReportSkeleton() {
  return (
    <div className="space-y-4" aria-hidden data-testid="report-skeleton">
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {[0, 1, 2, 3].map((i) => (
          <Skeleton key={i} className="h-24" />
        ))}
      </div>
      <Skeleton className="h-72" />
      <Skeleton className="h-56" />
    </div>
  );
}

/**
 * The frame every report shares: filter bar, export menu, the range problem, load and refusal states, and the notices. While new
 * filters load, the previous numbers stay (dimmed, busy) instead of flashing a blank page.
 */
export function ReportFrame<T extends { notes: string[]; filters: { branchCodes: string[] } }>({
  report,
  ctx,
  query,
  groupOptions,
  groupLabel,
  showDays,
  sections,
  flags,
  children,
}: {
  /** The report whose export menu is offered; none for the profit and loss (its export is the accountant panel). */
  report?: ReportName;
  ctx: ReportContext;
  query: UseQueryResult<T>;
  groupOptions?: SelectOption[];
  groupLabel?: string;
  showDays?: boolean;
  sections?: readonly string[];
  flags?: (data: T) => Flags;
  children: (data: T) => React.ReactNode;
}) {
  const { can } = usePermissions();
  const message = useProblemMessage();
  const canExport = report ? can(exportPermission(report), "anywhere") : false;
  const data = query.data;
  const refreshing = query.isFetching && !!data;
  return (
    <div className="space-y-4">
      <FilterBar ctx={ctx} groupOptions={groupOptions} groupLabel={groupLabel} showDays={showDays} />
      <div className="flex flex-wrap items-center justify-between gap-3">
        <GeneratedLine branchCodes={data?.filters.branchCodes ?? null} />
        {canExport && report ? <ExportMenu report={report} filters={ctx.filters} sections={sections} disabled={!ctx.ready} /> : null}
      </div>
      {ctx.problem ? <RangeAlert problem={ctx.problem} groupBy={effectiveGroupBy(ctx.filters)} /> : null}
      {query.isError && !ctx.problem ? (
        <Alert variant="danger" data-testid="report-error">
          {message(query.error instanceof ApiError ? query.error.problem : undefined)}
        </Alert>
      ) : null}
      {ctx.ready && query.isPending && !query.isError ? <ReportSkeleton /> : null}
      {data ? (
        <div className={cn("space-y-4 transition-opacity", refreshing && "opacity-60")} aria-busy={refreshing} data-testid="report-body">
          <Notices notes={data.notes} flags={flags?.(data)} />
          {children(data)}
        </div>
      ) : null}
    </div>
  );
}

function GeneratedLine({ branchCodes }: { branchCodes: string[] | null }) {
  const t = useTranslations("reports.filters");
  if (!branchCodes) return <span />;
  return (
    <p className="text-sm text-muted" data-testid="report-scope">
      {branchCodes.length === 0 ? t("scopeAll") : t("scopeBranches", { codes: branchCodes.join(", ") })}
    </p>
  );
}
