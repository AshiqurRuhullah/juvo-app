"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useFormatter, useTranslations } from "next-intl";
import { ArrowRight, TrendingDown, TrendingUp } from "lucide-react";
import type { UseQueryResult } from "@tanstack/react-query";
import { Button } from "@/components/ui/button";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { usePermissions } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { cn } from "@/lib/utils";
import { useCustomersReport, useFinanceReport, useInventoryReport, useSalesReport } from "@/modules/reports/api";
import { ChartCard, FilterBar, RangeAlert, TrendChart, Tiles, useReportFormat, type ReportContext } from "@/modules/reports/common";
import { bangladeshToday, num, presetRange, type ReportFilters } from "@/modules/reports/logic";
import { GROUPS, alignSeries, delta, ordersLink, parseRange, periodProblem, previousRange, rangeToSearch, splitByPayment, stockHealth, topBy, type DashboardRange, type Group } from "./period";
import type { DashboardLinks } from "./dashboard-view";

/** The part of the dashboard that follows the date range (FR-M03..M08). Every section loads its own report, so one slow or refused report never blanks the page. */

/* ---- Range in the address bar ----------------------------------------------------------------------------------------------- */

export function useDashboardRange(branchId: string | undefined) {
  const params = useSearchParams();
  const router = useRouter();
  const pathname = usePathname();
  const today = React.useMemo(() => bangladeshToday(), []);
  const range = React.useMemo(() => parseRange(params, today), [params, today]);
  const problem = periodProblem(range);
  const update = React.useCallback((patch: Partial<DashboardRange>) => router.replace(`${pathname}?${rangeToSearch({ ...range, ...patch })}`, { scroll: false }), [router, pathname, range]);
  const filters = React.useMemo<ReportFilters>(() => ({ from: range.from, to: range.to, branchId }), [range.from, range.to, branchId]);
  const ctx = React.useMemo<ReportContext>(
    () => ({ filters, today, kind: "plain", problem, ready: problem === null, change: (patch) => update({ from: patch.from ?? range.from, to: patch.to ?? range.to }) }),
    [filters, today, problem, update, range.from, range.to],
  );
  return { range, today, problem, update, filters, ctx };
}

/* ---- Card that owns one report's loading, empty and error states -------------------------------------------------------------- */

/** A refused report (403) hides the card with no message; any other failure shows a plain error inside the card. */
function ReportCard<T>({ query, title, description, testId, className, children }: { query: UseQueryResult<T>; title: string; description?: string; testId: string; className?: string; children: (data: T) => React.ReactNode }) {
  const message = useProblemMessage();
  const t = useTranslations("dashboard2.period");
  if (query.isError && !query.data && query.error instanceof ApiError && query.error.problem.status === 403) return null;
  return (
    <Card className={className} data-testid={testId}>
      <CardHeader>
        <CardTitle>{title}</CardTitle>
        {description ? <CardDescription>{description}</CardDescription> : null}
      </CardHeader>
      <CardContent aria-busy={query.isFetching && !!query.data}>
        {query.isError && !query.data ? (
          <Alert variant="danger" data-testid={`${testId}-error`}>
            {message(query.error instanceof ApiError ? query.error.problem : undefined)}
          </Alert>
        ) : !query.data ? (
          <div className="space-y-3" aria-label={t("loading")} data-testid={`${testId}-loading`}>
            <Skeleton className="h-6 w-2/3" />
            <Skeleton className="h-24" />
          </div>
        ) : (
          <div className={cn("transition-opacity", query.isFetching && "opacity-60")}>{children(query.data)}</div>
        )}
      </CardContent>
    </Card>
  );
}

type Rank = { id: string; label: string; value: number; valueText: string; sub?: string; href?: string };

/** A short ranked list with a bar behind each name. The numbers are written out, so the bar is never the only signal. */
function RankList({ rows, testId, emptyTitle, emptyText }: { rows: Rank[]; testId: string; emptyTitle: string; emptyText: string }) {
  if (rows.length === 0) return <EmptyState title={emptyTitle} description={emptyText} />;
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <ol className="space-y-4" data-testid={testId}>
      {rows.map((r, i) => {
        const body = (
          <>
            <span className="flex items-baseline justify-between gap-3 text-sm">
              <span className="min-w-0 truncate font-medium text-strong">
                <span className="me-2 text-muted tabular-nums">{i + 1}.</span>
                {r.label}
              </span>
              <span className="shrink-0 tabular-nums text-muted">{r.valueText}</span>
            </span>
            <span className="mt-1.5 block h-2 overflow-hidden rounded-full bg-sunken" aria-hidden>
              <span className="block h-full rounded-full bg-primary/40" style={{ width: `${Math.round((r.value / max) * 100)}%` }} />
            </span>
            {r.sub ? <span className="mt-1 block text-xs tabular-nums text-muted">{r.sub}</span> : null}
          </>
        );
        return (
          <li key={r.id}>
            {r.href ? (
              <Link href={r.href} className="block min-h-11 rounded-control [@media(hover:hover)]:hover:bg-sunken/60">
                {body}
              </Link>
            ) : (
              body
            )}
          </li>
        );
      })}
    </ol>
  );
}

const Delta = ({ value, label }: { value: { diff: number; percent: number | null }; label: string }) => {
  const format = useFormatter();
  const t = useTranslations("dashboard2.kpi");
  const up = value.diff >= 0;
  return value.percent === null ? (
    <span className="text-sm text-muted">{label}</span>
  ) : (
    <Badge variant={up ? "success" : "danger"}>
      {up ? <TrendingUp className="size-3.5" aria-hidden /> : <TrendingDown className="size-3.5" aria-hidden />}
      <span className="sr-only">{up ? t("up") : t("down")} </span>
      {up ? "+" : ""}
      {format.number(value.percent, { maximumFractionDigits: 1 })}%
    </Badge>
  );
};

/* ---- The sections ---------------------------------------------------------------------------------------------------------- */

function SalesTrend({ range, filters, update }: { range: DashboardRange; filters: ReportFilters; update: (patch: Partial<DashboardRange>) => void }) {
  const t = useTranslations("dashboard2.period");
  const ts = useTranslations("reports.sales");
  const tg = useTranslations("reports.groupBy");
  const f = useReportFormat();
  const prevRange = React.useMemo(() => previousRange(range.from, range.to), [range.from, range.to]);
  const cur = useSalesReport({ ...filters, groupBy: range.group }, true);
  const prev = useSalesReport({ ...filters, ...prevRange, groupBy: range.group }, range.compare);

  const shown = cur.data;
  const rows = React.useMemo(
    () => alignSeries((shown?.rows ?? []).map((r) => ({ label: r.label, net: num(r.net), orders: num(r.orders) })), range.compare && prev.data ? prev.data.rows.map((r) => ({ net: num(r.net) })) : null),
    [shown, prev.data, range.compare],
  );
  const havePrev = range.compare && !!prev.data;
  const series = [
    { key: "net", label: t("trend.current"), color: "var(--brand-primary)", kind: "area" as const },
    ...(havePrev ? [{ key: "previous", label: t("trend.previous"), color: "var(--info)", dashed: true, kind: "line" as const }] : []),
  ];
  const change = shown && prev.data ? delta(num(shown.totals.net), num(prev.data.totals.net)) : null;
  const ordersChange = shown && prev.data ? delta(num(shown.totals.orders), num(prev.data.totals.orders)) : null;

  return (
    <ReportCard query={cur} title={t("trend.title")} description={t("trend.subtitle", { from: f.dateOnly(range.from), to: f.dateOnly(range.to) })} testId="range-trend" className="xl:col-span-3">
      {(d) => {
        const best = rows.reduce<(typeof rows)[number] | null>((a, b) => (a === null || b.net > a.net ? b : a), null);
        const summary = !best || num(d.totals.orders) === 0 ? t("trend.none") : t("trend.summary", { count: rows.length, best: best.label, amount: f.money(best.net), total: f.money(d.totals.net) });
        return (
          <div className="space-y-4">
            <div className="flex flex-wrap items-center gap-3">
              <div role="group" aria-label={t("trend.groupLabel")} className="flex flex-wrap gap-2">
                {GROUPS.map((g: Group) => (
                  <Button key={g} variant={range.group === g ? "primary" : "secondary"} aria-pressed={range.group === g} data-testid={`group-${g}`} onClick={() => update({ group: g })}>
                    {tg(g)}
                  </Button>
                ))}
              </div>
              <Button variant={range.compare ? "primary" : "secondary"} aria-pressed={range.compare} data-testid="compare-toggle" onClick={() => update({ compare: !range.compare })}>
                {t("trend.compare")}
              </Button>
            </div>
            <Tiles
              testId="range-trend-tiles"
              tiles={[
                { key: "range-net", label: ts("columns.net"), value: f.money(d.totals.net) },
                { key: "range-orders", label: ts("columns.orders"), value: f.int(d.totals.orders) },
                ...(havePrev && change && ordersChange && prev.data
                  ? [
                      {
                        key: "range-net-change",
                        label: t("trend.changeNet"),
                        value: <Delta value={change} label={t("trend.noPrevious")} />,
                        hint: t("trend.wasAmount", { amount: f.money(prev.data.totals.net) }),
                      },
                      {
                        key: "range-orders-change",
                        label: t("trend.changeOrders"),
                        value: <Delta value={ordersChange} label={t("trend.noPrevious")} />,
                        hint: t("trend.wasOrders", { count: f.int(prev.data.totals.orders) }),
                      },
                    ]
                  : []),
              ]}
            />
            {range.compare && prev.isError ? <Alert variant="warning" data-testid="range-trend-prev-error">{t("trend.previousFailed")}</Alert> : null}
            {range.compare ? <p className="text-sm text-muted" data-testid="range-previous">{t("trend.previousRange", { from: f.dateOnly(prevRange.from), to: f.dateOnly(prevRange.to) })}</p> : null}
            <ChartCard
              title={ts("chart.title")}
              description={t("trend.chartHint")}
              summary={summary}
              legend={series}
              table={{ caption: t("trend.tableCaption"), headers: [tg(range.group), t("trend.current"), ...(havePrev ? [t("trend.previous")] : [])], rows: rows.map((r) => [r.label, f.money(r.net), ...(havePrev ? [r.previous === null ? "-" : f.money(r.previous)] : [])]) }}
            >
              <TrendChart data={rows} series={series} money={f.money} percent={f.pct} />
            </ChartCard>
          </div>
        );
      }}
    </ReportCard>
  );
}

function TopProducts({ filters, links }: { filters: ReportFilters; links: DashboardLinks }) {
  const t = useTranslations("dashboard2.period");
  const tt = useTranslations("dashboard2.top");
  const f = useReportFormat();
  const q = useSalesReport({ ...filters, groupBy: "product" }, true);
  return (
    <ReportCard query={q} title={t("products.title")} description={t("products.subtitle")} testId="range-top-products">
      {(d) => (
        <RankList
          testId="range-top-products-list"
          emptyTitle={t("products.emptyTitle")}
          emptyText={t("products.emptyText")}
          rows={topBy(d.rows, (r) => num(r.units), 5).filter((r) => num(r.units) > 0).map((r) => ({ id: r.key, label: r.label, value: num(r.units), valueText: tt("units", { count: num(r.units) }), sub: f.money(r.net), href: links.products ? `/admin/products?search=${encodeURIComponent(r.label)}` : undefined }))}
        />
      )}
    </ReportCard>
  );
}

function TopCategories({ filters }: { filters: ReportFilters }) {
  const t = useTranslations("dashboard2.period");
  const f = useReportFormat();
  const q = useSalesReport({ ...filters, groupBy: "category" }, true);
  return (
    <ReportCard query={q} title={t("categories.title")} description={t("categories.subtitle")} testId="range-top-categories">
      {(d) => (
        <RankList
          testId="range-top-categories-list"
          emptyTitle={t("categories.emptyTitle")}
          emptyText={t("categories.emptyText")}
          rows={topBy(d.rows, (r) => num(r.net), 5).filter((r) => num(r.net) > 0).map((r) => ({ id: r.key, label: r.label, value: num(r.net), valueText: f.money(r.net), sub: t("categories.units", { count: f.int(r.units) }) }))}
        />
      )}
    </ReportCard>
  );
}

function TopCustomers({ filters, links }: { filters: ReportFilters; links: DashboardLinks }) {
  const t = useTranslations("dashboard2.period");
  const f = useReportFormat();
  const q = useCustomersReport(filters, true);
  return (
    <ReportCard query={q} title={t("customers.title")} description={t("customers.subtitle")} testId="range-top-customers">
      {(d) => (
        <RankList
          testId="range-top-customers-list"
          emptyTitle={t("customers.emptyTitle")}
          emptyText={t("customers.emptyText")}
          rows={d.topSpenders.slice(0, 5).map((c) => ({ id: c.customerId, label: c.name || c.mobile, value: num(c.spent), valueText: f.money(c.spent), sub: `${c.name ? `${c.mobile} · ` : ""}${t("customers.orders", { count: f.int(c.orders) })}`, href: links.customers ? `/admin/customers/${c.customerId}` : undefined }))}
        />
      )}
    </ReportCard>
  );
}

function ByZone({ filters, range }: { filters: ReportFilters; range: DashboardRange }) {
  const t = useTranslations("dashboard2.period");
  const f = useReportFormat();
  const q = useSalesReport({ ...filters, groupBy: "zone" }, true);
  return (
    <ReportCard query={q} title={t("zones.title")} description={t("zones.subtitle")} testId="range-zones">
      {(d) => {
        const rows = topBy(d.rows, (r) => num(r.orders), 8).filter((r) => num(r.orders) > 0);
        return (
          <>
            <RankList testId="range-zones-list" emptyTitle={t("zones.emptyTitle")} emptyText={t("zones.emptyText")} rows={rows.map((r) => ({ id: r.key || "none", label: r.label, value: num(r.orders), valueText: t("zones.orders", { count: f.int(r.orders) }), sub: f.money(r.total) }))} />
            <p className="mt-3 text-xs text-muted" data-testid="range-zones-note">{t("zones.districtNote", { from: f.dateOnly(range.from), to: f.dateOnly(range.to) })}</p>
          </>
        );
      }}
    </ReportCard>
  );
}

function CashView({ filters, links, canSales }: { filters: ReportFilters; links: DashboardLinks; canSales: boolean }) {
  const t = useTranslations("dashboard2.period.cash");
  const f = useReportFormat();
  const fin = useFinanceReport(filters, true);
  const pay = useSalesReport({ ...filters, groupBy: "payment" }, canSales);
  const split = pay.data ? splitByPayment(pay.data.rows) : null;
  const link = (href: string | undefined, label: string) =>
    href ? (
      <Link href={href} className="inline-flex min-h-11 items-center gap-1 text-sm font-medium text-primary underline-offset-2 hover:underline">
        {label} <ArrowRight className="size-4 rtl:rotate-180" aria-hidden />
      </Link>
    ) : null;
  return (
    <ReportCard query={fin} title={t("title")} description={t("subtitle")} testId="range-cash">
      {(d) => (
        <div className="space-y-3">
          <Tiles
            testId="range-cash-tiles"
            tiles={[
              ...(split ? [{ key: "cash-online", label: t("online"), value: f.money(split.online.total), hint: t("onlineHint", { count: f.int(split.online.orders) }) }] : []),
              { key: "cash-cod-pending", label: t("codPending"), value: f.money(d.cod.pendingWithCouriers), hint: t("codPendingHint") },
              { key: "cash-cod-transit", label: t("codTransit"), value: f.money(d.cod.inTransit), hint: t("codTransitHint") },
              { key: "cash-cod-collected", label: t("codCollected"), value: f.money(d.cod.collected) },
              { key: "cash-cod-settled", label: t("codSettled"), value: f.money(d.cod.settled) },
              { key: "cash-refunds", label: t("refunds"), value: f.money(d.totals.refunds), hint: t("refundsHint") },
            ]}
          />
          <p className="text-xs text-muted" data-testid="range-cash-gap">{t("gap")}</p>
          <div className="flex flex-wrap gap-x-5">
            {link(links.cod ? "/admin/cod" : undefined, t("openCod"))}
            {link(links.payments ? "/admin/payments" : undefined, t("openPayments"))}
          </div>
        </div>
      )}
    </ReportCard>
  );
}

function StockHealth({ today, filters, links }: { today: string; filters: ReportFilters; links: DashboardLinks }) {
  const t = useTranslations("dashboard2.period.stock");
  const f = useReportFormat();
  // Stock is a snapshot of now: the range only matters for movements, which are not shown here, so the request does not change with the range.
  const q = useInventoryReport({ ...filters, ...presetRange("last30", today) }, true);
  return (
    <ReportCard query={q} title={t("title")} description={t("subtitle")} testId="range-stock">
      {(d) => {
        const h = stockHealth(d.lowStockCount, d.lowStock);
        const worst = topBy(d.lowStock, (r) => num(r.reorderLevel) - num(r.available), 4);
        const days = d.filters.days ?? 60;
        return (
          <div className="space-y-3">
            <Tiles
              testId="range-stock-tiles"
              tiles={[
                { key: "stock-low", label: t("low"), value: f.int(h.low), hint: t("lowHint") },
                { key: "stock-out", label: t("out"), value: `${h.exact ? "" : "≥ "}${f.int(h.out)}`, hint: h.exact ? t("outHint") : t("outCut") },
                { key: "stock-slow", label: t("slow"), value: f.int(d.deadStockCount), hint: t("slowHint", { days: f.int(days), value: f.money(d.deadStockValueAtPrice) }) },
              ]}
            />
            {worst.length > 0 ? (
              <ul className="divide-y divide-line/70 text-sm" data-testid="range-stock-low">
                {worst.map((r) => (
                  <li key={`${r.variantId}-${r.branchId}`} className="flex items-center justify-between gap-3 py-2">
                    <span className="min-w-0 truncate text-strong">{r.product} <span className="text-muted">({r.sku})</span></span>
                    <span className="shrink-0 tabular-nums text-muted">{t("row", { available: f.int(r.available), level: f.int(r.reorderLevel), branch: r.branchCode })}</span>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-sm text-muted" data-testid="range-stock-empty">{t("allGood")}</p>
            )}
            <p className="text-xs text-muted" data-testid="range-stock-gap">{t("gap")}</p>
            {links.inventory ? (
              <Link href="/admin/inventory" className="inline-flex min-h-11 items-center gap-1 text-sm font-medium text-primary underline-offset-2 hover:underline" data-testid="range-stock-link">
                {t("open")} <ArrowRight className="size-4 rtl:rotate-180" aria-hidden />
              </Link>
            ) : null}
          </div>
        );
      }}
    </ReportCard>
  );
}

/* ---- The whole range part --------------------------------------------------------------------------------------------------- */

export function RangeSections({ branchId, links }: { branchId?: string; links: DashboardLinks }) {
  const t = useTranslations("dashboard2.period");
  const { can } = usePermissions();
  const { range, today, problem, update, filters, ctx } = useDashboardRange(branchId);
  const canSales = can("reports.sales.view", "anywhere");
  const canFinance = can("reports.finance.view", "anywhere");
  const canInventory = can("reports.inventory.view", "anywhere");
  const canCustomers = can("reports.customers.view", "anywhere");
  const f = useReportFormat();
  return (
    <section aria-labelledby="period-title" className="space-y-4" data-testid="range-part" data-from={range.from} data-to={range.to}>
      <div>
        <h2 id="period-title" className="font-display text-xl font-semibold text-strong">{t("title")}</h2>
        <p className="text-sm text-muted">{t("subtitle", { from: f.dateOnly(range.from), to: f.dateOnly(range.to) })}</p>
      </div>
      <FilterBar ctx={ctx} hideBranch />
      {problem ? <RangeAlert problem={problem} groupBy={range.group} /> : null}
      {problem ? null : (
        <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
          {canSales ? <SalesTrend range={range} filters={filters} update={update} /> : null}
          {canSales ? <TopProducts filters={filters} links={links} /> : null}
          {canSales ? <TopCategories filters={filters} /> : null}
          {canCustomers ? <TopCustomers filters={filters} links={links} /> : null}
          {canSales ? <ByZone filters={filters} range={range} /> : null}
          {canFinance ? <CashView filters={filters} links={links} canSales={canSales} /> : null}
          {canInventory ? <StockHealth today={today} filters={filters} links={links} /> : null}
          {!canSales && !canFinance && !canInventory && !canCustomers ? (
            <p className="text-sm text-muted lg:col-span-2 xl:col-span-3" data-testid="range-none">{t("noReports")}</p>
          ) : null}
        </div>
      )}
      {links.orders ? (
        <p className="text-sm">
          <Link href={ordersLink({ from: range.from, to: range.to })} className="inline-flex min-h-11 items-center gap-1 font-medium text-primary underline-offset-2 hover:underline" data-testid="range-orders-link">
            {t("ordersInRange")} <ArrowRight className="size-4 rtl:rotate-180" aria-hidden />
          </Link>
        </p>
      ) : null}
    </section>
  );
}
