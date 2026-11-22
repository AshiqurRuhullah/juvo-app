"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { ArrowRight, Ban, Banknote, CircleCheck, ClipboardCheck, PackageCheck, PackageX, RotateCcw, ShieldAlert, Undo2, TriangleAlert, ShoppingBag, TrendingDown, TrendingUp, Truck, Wallet, ReceiptText } from "lucide-react";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { cn } from "@/lib/utils";
import { useMoney } from "@/modules/catalog/format";
import { useDashboard } from "@/modules/reports/api";
import { num } from "@/modules/reports/logic";
import { buildKpis, type Kpi } from "./logic";
import { ordersLink, statusCounts } from "./period";
import { RangeSections } from "./range-sections";

// Recharts is large: it loads after the page is usable and is drawn in the browser only.
const SalesChart = dynamic(() => import("./charts").then((m) => m.SalesChart), { ssr: false, loading: () => <Skeleton className="size-full" /> });
const StatusDonut = dynamic(() => import("./charts").then((m) => m.StatusDonut), { ssr: false, loading: () => <Skeleton className="size-full rounded-full" /> });
const Sparkline = dynamic(() => import("./charts").then((m) => m.Sparkline), { ssr: false, loading: () => <Skeleton className="size-full" /> });

/** Which screens the person may open from the action queue (decided on the server). */
export type DashboardLinks = { orders: boolean; fraud: boolean; returns: boolean; inventory: boolean; products: boolean; customers: boolean; payments: boolean; cod: boolean; shipments: boolean };

const STATUS_COLOUR: Record<string, string> = {
  Placed: "var(--text-muted)",
  Confirmed: "var(--info)",
  Packed: "var(--warning)",
  HandedToCourier: "var(--brand-primary)",
  InTransit: "var(--brand-primary)",
  OutForDelivery: "var(--brand-primary)",
  Delivered: "var(--success)",
  Cancelled: "var(--danger)",
  DeliveryFailed: "var(--danger)",
  ReturnInProgress: "var(--warning)",
  Returned: "var(--danger)",
};

const KPI_STYLE = {
  sales: { icon: Wallet, chip: "bg-primary-soft text-primary-dark", line: "var(--brand-primary)" },
  orders: { icon: ShoppingBag, chip: "bg-info-soft text-info-ink", line: "var(--info)" },
  average: { icon: ReceiptText, chip: "bg-warning-soft text-warning-ink", line: "var(--warning)" },
  toConfirm: { icon: ClipboardCheck, chip: "bg-success-soft text-success-ink", line: "var(--success)" },
} as const;

function KpiCard({ kpi }: { kpi: Kpi }) {
  const t = useTranslations("dashboard2.kpi");
  const tr = useTranslations("reports.dashboard");
  const format = useFormatter();
  const money = useMoney();
  const style = KPI_STYLE[kpi.key];
  const Icon = style.icon;
  const value = kpi.key === "sales" || kpi.key === "average" ? money(kpi.value) : format.number(kpi.value);
  const up = (kpi.change ?? 0) >= 0;
  return (
    <Card className="p-5" data-testid={`kpi-${kpi.key}`}>
      <p className="flex items-center gap-2.5 text-sm font-medium text-muted">
        <span className={cn("flex size-8 shrink-0 items-center justify-center rounded-control", style.chip)} aria-hidden>
          <Icon className="size-[1.125rem]" />
        </span>
        <span className="min-w-0">{kpi.key === "toConfirm" ? tr("toConfirm") : t(kpi.key)}</span>
      </p>
      <div className="mt-3 flex items-end justify-between gap-3">
        <p className="min-w-0 text-3xl font-bold tracking-tight text-strong tabular-nums" data-testid="kpi-value">
          {value}
        </p>
        {kpi.spark.length > 1 ? (
          <div className="h-10 w-24 shrink-0" aria-hidden>
            <Sparkline values={kpi.spark} color={style.line} />
          </div>
        ) : null}
      </div>
      <p className="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted">
        {kpi.key === "toConfirm" ? (
          tr("toConfirmHint")
        ) : kpi.change === null ? (
          tr("noYesterday")
        ) : (
          <>
            <Badge variant={up ? "success" : "danger"}>
              {up ? <TrendingUp className="size-3.5" aria-hidden /> : <TrendingDown className="size-3.5" aria-hidden />}
              <span className="sr-only">{up ? t("up") : t("down")} </span>
              {up ? "+" : ""}
              {format.number(kpi.change, { maximumFractionDigits: 1 })}%
            </Badge>
            {t("vsYesterday")}
          </>
        )}
      </p>
    </Card>
  );
}

/** FR-M01: to pack, in delivery, delivered, cancelled and returned. "To pack" is all open orders; the rest are orders placed in the last 30 days (the dashboard feed). */
function StatusStrip({ pack, counts, links }: { pack: number; counts: ReturnType<typeof statusCounts>; links: DashboardLinks }) {
  const t = useTranslations("dashboard2.strip");
  const format = useFormatter();
  const cards: { key: string; label: string; value: number; hint: string; icon: React.ComponentType<{ className?: string }>; href?: string }[] = [
    { key: "toPack", label: t("toPack"), value: pack, hint: t("openHint"), icon: PackageCheck, href: links.orders ? ordersLink({ status: "Confirmed" }) : undefined },
    { key: "inDelivery", label: t("inDelivery"), value: counts.inDelivery, hint: t("windowHint"), icon: Truck, href: links.shipments ? "/admin/shipments" : undefined },
    { key: "delivered", label: t("delivered"), value: counts.delivered, hint: t("windowHint"), icon: CircleCheck, href: links.orders ? ordersLink({ status: "Delivered" }) : undefined },
    { key: "cancelled", label: t("cancelled"), value: counts.cancelled, hint: t("windowHint"), icon: Ban, href: links.orders ? ordersLink({ status: "Cancelled" }) : undefined },
    { key: "returned", label: t("returned"), value: counts.returned, hint: t("windowHint"), icon: Undo2, href: links.returns ? "/admin/returns" : undefined },
  ];
  return (
    <ul className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5" data-testid="status-strip">
      {cards.map((c) => {
        const Icon = c.icon;
        const body = (
          <>
            <span className="flex items-center gap-2 text-sm font-medium text-muted"><Icon className="size-4 shrink-0" aria-hidden /> <span className="min-w-0">{c.label}</span></span>
            <span className="mt-2 block text-2xl font-bold tabular-nums text-strong" data-testid="strip-value">{format.number(c.value)}</span>
            <span className="mt-1 block text-xs text-muted">{c.hint}</span>
          </>
        );
        return (
          <li key={c.key} data-testid={`strip-${c.key}`}>
            {c.href ? (
              <Link href={c.href} className="block min-h-11 rounded-card border border-line bg-card p-4 shadow-card [@media(hover:hover)]:hover:bg-sunken/60">{body}</Link>
            ) : (
              <div className="rounded-card border border-line bg-card p-4 shadow-card">{body}</div>
            )}
          </li>
        );
      })}
    </ul>
  );
}

function DashboardSkeleton() {
  return (
    <div className="space-y-5" aria-hidden data-testid="dashboard-skeleton">
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {[0, 1, 2, 3].map((i) => (
          <Skeleton key={i} className="h-36" />
        ))}
      </div>
      <div className="grid gap-4 xl:grid-cols-3">
        <Skeleton className="h-96 xl:col-span-2" />
        <Skeleton className="h-96" />
      </div>
      <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
        <Skeleton className="h-72" />
        <Skeleton className="h-72" />
        <Skeleton className="h-72" />
      </div>
    </div>
  );
}

/** The dashboard: one call to the reports API, then everything on the page is drawn from that answer. */
export function DashboardView({ branchId, links }: { branchId?: string; links: DashboardLinks }) {
  const t = useTranslations("dashboard2");
  const tr = useTranslations("reports.dashboard");
  const ts = useTranslations("orders.status.order");
  const format = useFormatter();
  const money = useMoney();
  const message = useProblemMessage();
  const query = useDashboard(branchId);
  const data = query.data;

  const day = React.useCallback(
    (iso: string) => {
      const [y, m, d] = iso.slice(0, 10).split("-").map(Number);
      return format.dateTime(new Date(Date.UTC(y!, m! - 1, d!)), { day: "numeric", month: "short", timeZone: "UTC" });
    },
    [format],
  );

  const days = React.useMemo(() => (data ? data.series.map((p) => ({ label: day(p.date), sales: num(p.sales), orders: num(p.orders) })) : []), [data, day]);
  const kpis = React.useMemo(() => (data ? buildKpis(data.series, data.todayTotals, data.pending.toConfirm) : []), [data]);
  const statuses = React.useMemo(() => (data ? data.ordersByStatus.map((s) => ({ key: s.status, count: num(s.count) })).filter((s) => s.count > 0) : []), [data]);

  if (query.isError && !data) return <Alert variant="danger" data-testid="dashboard-error">{message(query.error instanceof ApiError ? query.error.problem : undefined)}</Alert>;
  if (!data) return <DashboardSkeleton />;

  const best = days.reduce((a, b) => (b.sales > a.sales ? b : a), days[0]!);
  const statusTotal = statuses.reduce((n, s) => n + s.count, 0);
  const donut = statuses.map((s) => ({ name: ts(s.key as "Placed"), value: s.count, color: STATUS_COLOUR[s.key] ?? "var(--text-muted)" }));
  const topMax = Math.max(1, ...data.topProducts.map((p) => num(p.units)));
  const summary = t("chart.summary", { count: days.length, from: day(data.series[0]!.date), to: day(data.today), best: best.label, amount: money(best.sales) });

  const counts = statusCounts(data.ordersByStatus);
  const actions: { key: "toConfirm" | "toPack" | "heldByFraud" | "returnsWaiting" | "lowStock" | "deliveryFailed"; count: number; href?: string; icon: React.ComponentType<{ className?: string }>; warn?: boolean }[] = [
    { key: "toConfirm", count: num(data.pending.toConfirm), href: links.orders ? "/admin/orders?status=Placed" : undefined, icon: ClipboardCheck },
    { key: "toPack", count: num(data.pending.toPack), href: links.orders ? "/admin/orders?status=Confirmed" : undefined, icon: PackageCheck },
    { key: "deliveryFailed", count: counts.failed, href: links.orders ? ordersLink({ status: "DeliveryFailed" }) : undefined, icon: TriangleAlert, warn: true },
    { key: "heldByFraud", count: num(data.pending.heldByFraud), href: links.fraud ? "/admin/fraud" : undefined, icon: ShieldAlert, warn: true },
    { key: "returnsWaiting", count: num(data.pending.returnsWaiting), href: links.returns ? "/admin/returns" : undefined, icon: RotateCcw },
    { key: "lowStock", count: num(data.pending.lowStock), href: links.inventory ? "/admin/inventory" : undefined, icon: PackageX, warn: true },
  ];

  return (
    <div className="space-y-5" data-testid="dashboard" data-today={data.today} data-branch={branchId ?? ""}>
      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {kpis.map((kpi) => (
          <KpiCard key={kpi.key} kpi={kpi} />
        ))}
      </div>

      <StatusStrip pack={num(data.pending.toPack)} counts={counts} links={links} />

      <div className="grid gap-4 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader>
            <CardTitle>{t("chart.title")}</CardTitle>
            <CardDescription>{t("chart.subtitle", { count: days.length })}</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="mb-3 flex flex-wrap gap-x-5 gap-y-1 text-sm text-muted" aria-hidden>
              <li className="flex items-center gap-2"><span className="h-0.5 w-5 rounded-full bg-primary" /> {t("chart.sales")}</li>
              <li className="flex items-center gap-2"><span className="h-0.5 w-5 rounded-full border-t-2 border-dashed border-info" /> {t("chart.orders")}</li>
            </ul>
            <div role="img" aria-label={summary} className="h-64 w-full sm:h-72">
              <SalesChart data={days} salesLabel={t("chart.sales")} ordersLabel={t("chart.orders")} money={money} />
            </div>
            <div className="sr-only">
            <table data-testid="dashboard-series">
              <caption>{t("chart.tableCaption")}</caption>
              <thead>
                <tr><th scope="col">{t("chart.date")}</th><th scope="col">{t("chart.sales")}</th><th scope="col">{t("chart.orders")}</th></tr>
              </thead>
              <tbody>
                {data.series.map((p, i) => (
                  <tr key={p.date}><th scope="row">{days[i]!.label}</th><td>{money(p.sales)}</td><td>{num(p.orders)}</td></tr>
                ))}
              </tbody>
            </table>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("status.title")}</CardTitle>
            <CardDescription>{t("status.subtitle", { count: statusTotal })}</CardDescription>
          </CardHeader>
          <CardContent>
            {statusTotal === 0 ? (
              <p className="py-8 text-center text-muted" data-testid="status-empty">{tr("noOrders")}</p>
            ) : (
              <>
                <div className="relative mx-auto h-44 w-44" role="img" aria-label={t("status.summary", { count: statusTotal })}>
                  <StatusDonut data={donut} />
                  <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center" aria-hidden>
                    <span className="text-2xl font-bold tabular-nums text-strong">{statusTotal}</span>
                    <span className="text-xs text-muted">{t("status.orders")}</span>
                  </div>
                </div>
                <ul className="mt-4 grid grid-cols-1 gap-x-4 gap-y-2 text-sm sm:grid-cols-2" data-testid="status-list">
                  {statuses.map((s) => (
                    <li key={s.key} className="flex items-center justify-between gap-2" data-status={s.key}>
                      <span className="flex min-w-0 items-center gap-2 text-muted">
                        <span className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: STATUS_COLOUR[s.key] }} aria-hidden />
                        <span className="truncate">{ts(s.key as "Placed")}</span>
                      </span>
                      <span className="font-medium tabular-nums text-strong">{s.count}</span>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </CardContent>
        </Card>
      </div>

      <div className="grid gap-4 lg:grid-cols-2 xl:grid-cols-3">
        <Card>
          <CardHeader>
            <CardTitle>{t("actions.title")}</CardTitle>
            <CardDescription>{t("actions.subtitle")}</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="divide-y divide-line/70" data-testid="action-queue">
              {actions.map((a) => {
                const Icon = a.icon;
                const body = (
                  <>
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-control bg-sunken text-muted" aria-hidden>
                      <Icon className="size-[1.125rem]" />
                    </span>
                    <span className="min-w-0 flex-1 text-strong">{tr(`actions.${a.key}`)}</span>
                    <Badge variant={a.warn && a.count > 0 ? "warning" : "neutral"} className="tabular-nums" data-testid={`action-count-${a.key}`}>{a.count}</Badge>
                    {a.href ? <ArrowRight className="size-4 shrink-0 text-muted rtl:rotate-180" aria-hidden /> : null}
                  </>
                );
                return (
                  <li key={a.key}>
                    {a.href ? (
                      <Link href={a.href} data-testid={`action-${a.key}`} className="flex min-h-14 items-center gap-3 rounded-control px-1 py-2 [@media(hover:hover)]:hover:bg-sunken/60">
                        {body}
                      </Link>
                    ) : (
                      <div className="flex min-h-14 items-center gap-3 px-1 py-2" data-testid={`action-${a.key}`}>{body}</div>
                    )}
                  </li>
                );
              })}
            </ul>
            {links.payments || links.shipments ? (
              <div className="mt-3 border-t border-line/70 pt-3" data-testid="action-screens">
                <p className="text-xs text-muted">{tr("actions.noCounts")}</p>
                <ul className="mt-1 flex flex-wrap gap-x-5">
                  {links.payments ? (
                    <li><Link href="/admin/payments" className="inline-flex min-h-11 items-center gap-1 text-sm font-medium text-primary underline-offset-2 hover:underline" data-testid="action-payments-screen">{tr("actions.paymentsScreen")} <ArrowRight className="size-4 rtl:rotate-180" aria-hidden /></Link></li>
                  ) : null}
                  {links.shipments ? (
                    <li><Link href="/admin/shipments" className="inline-flex min-h-11 items-center gap-1 text-sm font-medium text-primary underline-offset-2 hover:underline" data-testid="action-shipments-screen">{tr("actions.bookingsScreen")} <ArrowRight className="size-4 rtl:rotate-180" aria-hidden /></Link></li>
                  ) : null}
                </ul>
              </div>
            ) : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("top.title")}</CardTitle>
            <CardDescription>{tr("topSubtitle")}</CardDescription>
          </CardHeader>
          <CardContent>
            {data.topProducts.length === 0 ? (
              <EmptyState title={tr("noTopTitle")} description={tr("noTopText")} />
            ) : (
              <ol className="space-y-4" data-testid="top-products">
                {data.topProducts.map((p, i) => (
                  <li key={p.productId}>
                    <div className="flex items-baseline justify-between gap-3 text-sm">
                      <span className="min-w-0 truncate font-medium text-strong">
                        <span className="me-2 text-muted tabular-nums">{i + 1}.</span>
                        {p.name}
                      </span>
                      <span className="shrink-0 tabular-nums text-muted">{t("top.units", { count: num(p.units) })}</span>
                    </div>
                    <div className="mt-1.5 h-2 overflow-hidden rounded-full bg-sunken" aria-hidden>
                      <div className="h-full rounded-full bg-primary/40" style={{ width: `${Math.round((num(p.units) / topMax) * 100)}%` }} />
                    </div>
                    <p className="mt-1 text-xs tabular-nums text-muted">{money(p.revenue)}</p>
                  </li>
                ))}
              </ol>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Banknote className="size-5 text-muted" aria-hidden /> {t("cash.title")}
            </CardTitle>
            <CardDescription>{tr("cashSubtitle")}</CardDescription>
          </CardHeader>
          <CardContent>
            <dl className="divide-y divide-line/70 text-sm">
              <div className="flex items-center justify-between gap-3 py-2.5">
                <dt className="flex items-center gap-2 text-muted"><Truck className="size-4" aria-hidden /> {tr("codInTransit")}</dt>
                <dd className="font-medium tabular-nums text-strong" data-testid="cod-in-transit">{money(data.codInTransit)}</dd>
              </div>
              <div className="flex items-center justify-between gap-3 py-2.5">
                <dt className="flex items-center gap-2 text-muted"><Wallet className="size-4" aria-hidden /> {tr("codPending")}</dt>
                <dd className="font-medium tabular-nums text-strong" data-testid="cod-pending">{money(data.codPending)}</dd>
              </div>
            </dl>
            {data.notes.length > 0 ? (
              <ul className="mt-3 space-y-1 text-xs text-muted">
                {data.notes.map((n) => (
                  <li key={n}>{n}</li>
                ))}
              </ul>
            ) : null}
          </CardContent>
        </Card>
      </div>

      <RangeSections branchId={branchId} links={links} />
    </div>
  );
}
