"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { ArrowRight, Banknote, Boxes, ClipboardCheck, CreditCard, Headset, PackageX, ReceiptText, RotateCcw, ShoppingBag, TrendingDown, TrendingUp, UserPlus, Wallet } from "lucide-react";
import { DataTable } from "@/components/ui/data-table";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { useTableLabels } from "@/components/data/list-kit";
import { useMoney } from "@/modules/catalog/format";
import { cn } from "@/lib/utils";
import type { ActionKey, DashboardData, Kpi, RecentOrder, StatusKey, TrendKey } from "./sample-data";

// Recharts is large: it loads after the page is usable and is drawn in the browser only.
const SalesChart = dynamic(() => import("./charts").then((m) => m.SalesChart), { ssr: false, loading: () => <Skeleton className="size-full" /> });
const StatusDonut = dynamic(() => import("./charts").then((m) => m.StatusDonut), { ssr: false, loading: () => <Skeleton className="size-full rounded-full" /> });
const Sparkline = dynamic(() => import("./charts").then((m) => m.Sparkline), { ssr: false, loading: () => <Skeleton className="size-full" /> });

const KPI_STYLE: Record<TrendKey, { icon: React.ComponentType<{ className?: string }>; chip: string; line: string }> = {
  sales: { icon: Wallet, chip: "bg-primary-soft text-primary-dark", line: "var(--brand-primary)" },
  orders: { icon: ShoppingBag, chip: "bg-info-soft text-info-ink", line: "var(--info)" },
  average: { icon: ReceiptText, chip: "bg-warning-soft text-warning-ink", line: "var(--warning)" },
  customers: { icon: UserPlus, chip: "bg-success-soft text-success-ink", line: "var(--success)" },
};

const STATUS_COLOUR: Record<StatusKey, string> = {
  placed: "var(--text-muted)",
  confirmed: "var(--info)",
  packed: "var(--warning)",
  inTransit: "var(--brand-primary)",
  delivered: "var(--success)",
  returned: "var(--danger)",
};

const ACTION_ICON: Record<ActionKey, React.ComponentType<{ className?: string }>> = {
  confirm: ClipboardCheck,
  verifyPayments: CreditCard,
  lowStock: PackageX,
  returns: RotateCcw,
  tickets: Headset,
};

function SampleBadge() {
  const t = useTranslations("dashboard2");
  return (
    <Badge variant="info" title={t("sampleNote")}>
      {t("sample")}
    </Badge>
  );
}

function KpiCard({ kpi }: { kpi: Kpi }) {
  const t = useTranslations("dashboard2.kpi");
  const money = useMoney();
  const style = KPI_STYLE[kpi.key];
  const Icon = style.icon;
  const up = kpi.change >= 0;
  const value = kpi.key === "sales" || kpi.key === "average" ? money(kpi.value) : new Intl.NumberFormat().format(kpi.value);
  return (
    <Card className="p-5">
      <p className="flex items-center gap-2.5 text-sm font-medium text-muted">
        <span className={cn("flex size-8 shrink-0 items-center justify-center rounded-control", style.chip)} aria-hidden>
          <Icon className="size-[1.125rem]" />
        </span>
        <span className="min-w-0">{t(kpi.key)}</span>
      </p>
      <div className="mt-3 flex items-end justify-between gap-3">
        <p className="min-w-0 text-3xl font-bold tracking-tight text-strong tabular-nums">{value}</p>
        <div className="h-10 w-24 shrink-0" aria-hidden>
          <Sparkline values={kpi.spark} color={style.line} />
        </div>
      </div>
      <p className="mt-3 flex flex-wrap items-center gap-x-2 gap-y-1 text-sm text-muted">
        <Badge variant={up ? "success" : "danger"}>
          {up ? <TrendingUp className="size-3.5" aria-hidden /> : <TrendingDown className="size-3.5" aria-hidden />}
          <span className="sr-only">{up ? t("up") : t("down")} </span>
          {up ? "+" : ""}
          {kpi.change.toFixed(1)}%
        </Badge>
        {t("vsYesterday")}
      </p>
    </Card>
  );
}

/** The dashboard. Everything on it is sample data (see sample-data.ts) until the real reports exist. */
export function DashboardView({ data, todayIso }: { data: DashboardData; todayIso: string }) {
  const t = useTranslations("dashboard2");
  const format = useFormatter();
  const money = useMoney();
  const labels = useTableLabels();

  const days = React.useMemo(() => data.days.map((d) => ({ label: format.dateTime(new Date(d.date), { day: "numeric", month: "short" }), sales: d.sales, orders: d.orders })), [data.days, format]);
  const best = data.days.reduce((a, b) => (b.sales > a.sales ? b : a));
  const statusTotal = data.statuses.reduce((n, s) => n + s.count, 0);
  const donut = data.statuses.map((s) => ({ name: t(`status.${s.key}`), value: s.count, color: STATUS_COLOUR[s.key] }));
  const topMax = Math.max(...data.topProducts.map((p) => p.units));
  const stockTotal = data.stock.healthy + data.stock.low + data.stock.out;

  const columns = React.useMemo<ColumnDef<RecentOrder, unknown>[]>(
    () => [
      {
        id: "order",
        header: t("recent.order"),
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="flex flex-col">
            <span className="font-medium text-strong tabular-nums">{row.original.number}</span>
            <span className="text-sm text-muted">{row.original.customer}</span>
          </span>
        ),
      },
      { id: "customer", header: t("recent.customer"), meta: { hideOnMobile: true }, cell: ({ row }) => row.original.customer },
      { id: "total", header: t("recent.total"), meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="font-medium">{money(row.original.total)}</span> },
      {
        id: "payment",
        header: t("recent.payment"),
        meta: { secondary: true },
        cell: ({ row }) => <Badge variant={row.original.payment === "paid" ? "success" : row.original.payment === "cod" ? "neutral" : "warning"}>{t(`recent.paymentStatus.${row.original.payment}`)}</Badge>,
      },
      {
        id: "delivery",
        header: t("recent.delivery"),
        meta: { secondary: true },
        cell: ({ row }) => {
          const d = row.original.delivery;
          return <Badge variant={d === "delivered" ? "success" : d === "inTransit" ? "info" : d === "packed" ? "warning" : "neutral"}>{t(`recent.deliveryStatus.${d}`)}</Badge>;
        },
      },
      {
        id: "time",
        header: t("recent.time"),
        meta: { hideOnMobile: true },
        cell: ({ row }) => <span className="text-muted">{format.dateTime(new Date(row.original.createdAt), { hour: "2-digit", minute: "2-digit" })}</span>,
      },
    ],
    [t, money, format],
  );

  const summary = t("chart.summary", {
    count: data.days.length,
    from: format.dateTime(new Date(data.days[0]!.date), { day: "numeric", month: "short" }),
    to: format.dateTime(new Date(todayIso), { day: "numeric", month: "short" }),
    best: format.dateTime(new Date(best.date), { day: "numeric", month: "short" }),
    amount: money(best.sales),
  });

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center gap-2 text-sm text-muted">
        <SampleBadge />
        <span>{t("sampleNote")}</span>
      </div>

      <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
        {data.kpis.map((kpi) => (
          <KpiCard key={kpi.key} kpi={kpi} />
        ))}
      </div>

      <div className="grid gap-4 xl:grid-cols-3">
        <Card className="xl:col-span-2">
          <CardHeader>
            <CardTitle>{t("chart.title")}</CardTitle>
            <CardDescription>{t("chart.subtitle", { count: data.days.length })}</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="mb-3 flex flex-wrap gap-x-5 gap-y-1 text-sm text-muted" aria-hidden>
              <li className="flex items-center gap-2"><span className="h-0.5 w-5 rounded-full bg-primary" /> {t("chart.sales")}</li>
              <li className="flex items-center gap-2"><span className="h-0.5 w-5 rounded-full border-t-2 border-dashed border-info" /> {t("chart.orders")}</li>
            </ul>
            <div role="img" aria-label={summary} className="h-64 w-full sm:h-72">
              <SalesChart data={days} salesLabel={t("chart.sales")} ordersLabel={t("chart.orders")} money={money} />
            </div>
            <table className="sr-only">
              <caption>{t("chart.tableCaption")}</caption>
              <thead>
                <tr><th scope="col">{t("chart.date")}</th><th scope="col">{t("chart.sales")}</th><th scope="col">{t("chart.orders")}</th></tr>
              </thead>
              <tbody>
                {days.map((d) => (
                  <tr key={d.label}><th scope="row">{d.label}</th><td>{money(d.sales)}</td><td>{d.orders}</td></tr>
                ))}
              </tbody>
            </table>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("status.title")}</CardTitle>
            <CardDescription>{t("status.subtitle", { count: statusTotal })}</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="relative mx-auto h-44 w-44" role="img" aria-label={t("status.summary", { count: statusTotal })}>
              <StatusDonut data={donut} />
              <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center" aria-hidden>
                <span className="text-2xl font-bold tabular-nums text-strong">{statusTotal}</span>
                <span className="text-xs text-muted">{t("status.orders")}</span>
              </div>
            </div>
            <ul className="mt-4 grid grid-cols-2 gap-x-4 gap-y-2 text-sm">
              {data.statuses.map((s) => (
                <li key={s.key} className="flex items-center justify-between gap-2">
                  <span className="flex min-w-0 items-center gap-2 text-muted">
                    <span className="size-2.5 shrink-0 rounded-full" style={{ backgroundColor: STATUS_COLOUR[s.key] }} aria-hidden />
                    <span className="truncate">{t(`status.${s.key}`)}</span>
                  </span>
                  <span className="font-medium tabular-nums text-strong">{s.count}</span>
                </li>
              ))}
            </ul>
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
            <ul className="divide-y divide-line/70">
              {data.actions.map((a) => {
                const Icon = ACTION_ICON[a.key];
                const body = (
                  <>
                    <span className="flex size-9 shrink-0 items-center justify-center rounded-control bg-sunken text-muted" aria-hidden>
                      <Icon className="size-[1.125rem]" />
                    </span>
                    <span className="min-w-0 flex-1 text-strong">{t(`actions.${a.key}`)}</span>
                    <Badge variant={a.key === "lowStock" || a.key === "verifyPayments" ? "warning" : "neutral"} className="tabular-nums">{a.count}</Badge>
                    {a.href ? <ArrowRight className="size-4 shrink-0 text-muted rtl:rotate-180" aria-hidden /> : null}
                  </>
                );
                return (
                  <li key={a.key}>
                    {a.href ? (
                      <Link href={a.href} className="flex min-h-14 items-center gap-3 rounded-control px-1 py-2 [@media(hover:hover)]:hover:bg-sunken/60">
                        {body}
                      </Link>
                    ) : (
                      <div className="flex min-h-14 items-center gap-3 px-1 py-2" title={t("actions.soon")}>{body}</div>
                    )}
                  </li>
                );
              })}
            </ul>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("top.title")}</CardTitle>
            <CardDescription>{t("top.subtitle")}</CardDescription>
          </CardHeader>
          <CardContent>
            <ol className="space-y-4">
              {data.topProducts.map((p, i) => (
                <li key={p.name}>
                  <div className="flex items-baseline justify-between gap-3 text-sm">
                    <span className="min-w-0 truncate font-medium text-strong">
                      <span className="me-2 text-muted tabular-nums">{i + 1}.</span>
                      {p.name}
                    </span>
                    <span className="shrink-0 tabular-nums text-muted">{t("top.units", { count: p.units })}</span>
                  </div>
                  <div className="mt-1.5 h-2 overflow-hidden rounded-full bg-sunken" aria-hidden>
                    <div className="h-full rounded-full bg-primary/40" style={{ width: `${Math.round((p.units / topMax) * 100)}%` }} />
                  </div>
                  <p className="mt-1 text-xs tabular-nums text-muted">{money(p.revenue)}</p>
                </li>
              ))}
            </ol>
          </CardContent>
        </Card>

        <div className="grid gap-4 lg:col-span-2 lg:grid-cols-2 xl:col-span-1 xl:grid-cols-1">
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Banknote className="size-5 text-muted" aria-hidden /> {t("cash.title")}
              </CardTitle>
            </CardHeader>
            <CardContent>
              <dl className="divide-y divide-line/70 text-sm">
                {(["collectedToday", "onlineToday", "codWithCouriers", "refundsPending"] as const).map((k) => (
                  <div key={k} className="flex items-center justify-between gap-3 py-2.5">
                    <dt className="text-muted">{t(`cash.${k}`)}</dt>
                    <dd className="font-medium tabular-nums text-strong">{money(data.cash[k])}</dd>
                  </div>
                ))}
              </dl>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Boxes className="size-5 text-muted" aria-hidden /> {t("stock.title")}
              </CardTitle>
              <CardDescription>{t("stock.total", { count: stockTotal })}</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              <div className="flex h-3 overflow-hidden rounded-full bg-sunken" role="img" aria-label={t("stock.summary", { healthy: data.stock.healthy, low: data.stock.low, out: data.stock.out })}>
                <div className="bg-success/70" style={{ width: `${(data.stock.healthy / stockTotal) * 100}%` }} />
                <div className="bg-warning/70" style={{ width: `${(data.stock.low / stockTotal) * 100}%` }} />
                <div className="bg-danger/70" style={{ width: `${(data.stock.out / stockTotal) * 100}%` }} />
              </div>
              <ul className="flex flex-wrap gap-2 text-sm">
                <li><Badge variant="success">{t("stock.healthy")} · {data.stock.healthy}</Badge></li>
                <li><Badge variant="warning">{t("stock.low")} · {data.stock.low}</Badge></li>
                <li><Badge variant="danger">{t("stock.out")} · {data.stock.out}</Badge></li>
              </ul>
            </CardContent>
          </Card>
        </div>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("recent.title")}</CardTitle>
          <CardDescription>{t("recent.subtitle")}</CardDescription>
        </CardHeader>
        <CardContent>
          <DataTable caption={t("recent.title")} columns={columns} data={data.recent} getRowId={(o) => o.id} labels={labels} />
        </CardContent>
      </Card>
    </div>
  );
}
