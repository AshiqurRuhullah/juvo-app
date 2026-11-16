"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import { Info } from "lucide-react";
import { usePermissions } from "@/components/providers/session-provider";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { usePnl, type PnlBucket } from "./api";
import { ChartCard, ReportFrame, ReportTable, Section, Tiles, TrendChart, useGroupOptions, useReportFilters, useReportFormat } from "./common";
import { MAX_DAYS_DAILY, num, shapeSeries } from "./logic";
import { numCol, textCol } from "./table-kit";

const AccountantExport = dynamic(() => import("./accountant-export").then((m) => m.AccountantExport), { ssr: false, loading: () => <Skeleton className="h-40" /> });

type Category = { categoryId: string; category: string; amount: number | string; count: number | string };

/** Profit and loss (FR-X04): the period, a series, the expenses by category, purchases as information only, and the accountant export. */
export function PnlView() {
  const t = useTranslations("reports.pnl");
  const tg = useTranslations("reports.groupBy");
  const ctx = useReportFilters("pnl");
  const query = usePnl(ctx.filters, ctx.ready);
  const f = useReportFormat();
  const options = useGroupOptions("pnl");
  const { can } = usePermissions();
  const groupBy = query.data?.filters.groupBy ?? ctx.filters.groupBy ?? "day";
  const hidden = query.data?.costHidden ?? false;

  const columns = React.useMemo(
    () => [
      textCol<PnlBucket>("label", tg(groupBy as "day"), (r) => <span className="font-medium text-strong">{r.label}</span>, { primary: true }),
      numCol<PnlBucket>("orders", t("columns.orders"), (r) => f.int(r.orders), { hideOnMobile: true }),
      numCol<PnlBucket>("net", t("columns.netRevenue"), (r) => f.money(r.netRevenue)),
      ...(hidden ? [] : [numCol<PnlBucket>("cost", t("columns.costOfGoods"), (r) => f.cost(r.costOfGoods), { hideOnMobile: true }), numCol<PnlBucket>("gross", t("columns.grossProfit"), (r) => f.cost(r.grossProfit), { hideOnMobile: true })]),
      numCol<PnlBucket>("delivery", t("columns.deliveryCollected"), (r) => f.money(r.deliveryCollected), { hideOnMobile: true }),
      numCol<PnlBucket>("courier", t("columns.courierCharges"), (r) => f.money(r.courierCharges), { hideOnMobile: true }),
      numCol<PnlBucket>("expenses", t("columns.expenses"), (r) => f.money(r.expenses)),
      ...(hidden ? [] : [numCol<PnlBucket>("netProfit", t("columns.netProfit"), (r) => <span className="font-medium">{f.cost(r.netProfit)}</span>)]),
    ],
    [t, tg, groupBy, f, hidden],
  );
  const categories = React.useMemo(
    () => [
      textCol<Category>("category", t("categories.category"), (r) => <span className="font-medium text-strong">{r.category}</span>, { primary: true }),
      numCol<Category>("count", t("categories.count"), (r) => f.int(r.count)),
      numCol<Category>("amount", t("categories.amount"), (r) => f.money(r.amount)),
    ],
    [t, f],
  );

  return (
    <ReportFrame ctx={ctx} query={query} groupOptions={options} groupLabel={t("groupLabel")} flags={(d) => ({ costHidden: d.costHidden, gatewayFeesRecorded: d.total.gatewayFeesRecorded, costMissingLines: num(d.total.costMissingLines) })}>
      {(d) => {
        const tot = d.total;
        const points = shapeSeries(d.series.map((r) => ({ label: r.label, netRevenue: r.netRevenue, deliveryCollected: r.deliveryCollected, grossProfit: r.grossProfit, netProfit: r.netProfit, orders: r.orders, expenses: r.expenses })));
        const series = [
          { key: "revenue", label: t("columns.netRevenue"), color: "var(--brand-primary)" },
          { key: "expenses", label: t("columns.expenses"), color: "var(--warning)", dashed: true },
          ...(d.costHidden ? [] : [{ key: "profit", label: t("columns.grossProfit"), color: "var(--info)" }, { key: "net", label: t("columns.netProfit"), color: "var(--success)" }]),
        ];
        const best = points.reduce<(typeof points)[number] | null>((a, b) => (a === null || b.revenue > a.revenue ? b : a), null);
        const hiddenValue = <span className="text-base font-medium text-muted">{t("costHiddenValue")}</span>;
        const gateway = tot.gatewayFeesRecorded ? f.money(tot.gatewayFees) : <span className="text-base font-medium text-muted">{t("notRecorded")}</span>;
        return (
          <>
            <Tiles
              tiles={[
                { key: "net", label: t("columns.netRevenue"), value: f.money(tot.netRevenue), hint: t("hints.netRevenue") },
                { key: "cost", label: t("columns.costOfGoods"), value: tot.costOfGoods === null ? hiddenValue : f.money(tot.costOfGoods) },
                { key: "gross", label: t("columns.grossProfit"), value: tot.grossProfit === null ? hiddenValue : f.money(tot.grossProfit) },
                { key: "delivery", label: t("columns.deliveryCollected"), value: f.money(tot.deliveryCollected) },
                { key: "courier", label: t("columns.courierCharges"), value: f.money(tot.courierCharges) },
                { key: "fees", label: t("columns.gatewayFees"), value: gateway },
                { key: "expenses", label: t("columns.expenses"), value: f.money(tot.expenses), hint: t("hints.expenses") },
                { key: "netProfit", label: t("columns.netProfit"), value: tot.netProfit === null ? hiddenValue : f.money(tot.netProfit), emphasis: true, hint: t("hints.netProfit") },
              ]}
            />
            <ChartCard
              title={t("chart.title")}
              description={t("chart.subtitle")}
              summary={best ? t("chart.summary", { count: points.length, from: f.dateOnly(d.filters.from), to: f.dateOnly(d.filters.to), best: best.label, amount: f.money(best.revenue) }) : t("chart.none")}
              legend={series}
              table={{
                caption: t("chart.tableCaption"),
                headers: [tg(groupBy as "day"), t("columns.netRevenue"), t("columns.expenses"), ...(d.costHidden ? [] : [t("columns.grossProfit"), t("columns.netProfit")])],
                rows: points.map((p) => [p.label, f.money(p.revenue), f.money(p.expenses), ...(d.costHidden ? [] : [f.cost(p.profit), f.cost(p.net)])]),
              }}
            >
              <TrendChart data={points} series={series} money={f.money} percent={f.pct} />
            </ChartCard>
            <div className="grid grid-cols-[minmax(0,1fr)] gap-4 xl:grid-cols-3">
              <div className="min-w-0 xl:col-span-2">
                <Section title={t("table.title")} description={t("table.subtitle")} testId="pnl-table">
                  <ReportTable caption={t("table.title")} columns={columns} rows={d.series} getRowId={(r, i) => `${r.start}-${i}`} />
                </Section>
              </div>
              <div className="min-w-0 space-y-4">
                <Section title={t("categories.title")} description={t("categories.subtitle")} testId="pnl-categories">
                  <ReportTable caption={t("categories.title")} columns={categories} rows={d.expensesByCategory} getRowId={(r) => r.categoryId} />
                </Section>
                <Section title={t("purchases.title")} testId="pnl-purchases">
                  <p className="text-2xl font-semibold tabular-nums text-strong" data-testid="purchases-received">
                    {f.money(tot.purchasesReceived)}
                  </p>
                  <Alert variant="info" className="mt-3">
                    <span className="flex items-start gap-2">
                      <Info className="mt-0.5 size-4 shrink-0" aria-hidden />
                      {t("purchases.note")}
                    </span>
                  </Alert>
                </Section>
              </div>
            </div>
            {can("accounting.export.run", "anywhere") ? (
              <Section title={t("export.title")} description={t("export.subtitle", { days: MAX_DAYS_DAILY })} testId="pnl-export">
                <AccountantExport filters={ctx.filters} />
              </Section>
            ) : null}
          </>
        );
      }}
    </ReportFrame>
  );
}
