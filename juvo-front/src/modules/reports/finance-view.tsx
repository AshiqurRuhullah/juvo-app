"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useFinanceReport, type FinanceBucket } from "./api";
import { ChartCard, ReportFrame, ReportTable, Section, Tiles, TrendChart, useGroupOptions, useReportFilters, useReportFormat } from "./common";
import { SECTIONS, num, shapeSeries } from "./logic";
import { numCol, textCol } from "./table-kit";

/** Finance (FR-P04): revenue, discounts, refunds, delivery collected against courier charges, gross profit, and the COD position. */
export function FinanceView() {
  const t = useTranslations("reports.finance");
  const tg = useTranslations("reports.groupBy");
  const ctx = useReportFilters("finance");
  const query = useFinanceReport(ctx.filters, ctx.ready);
  const f = useReportFormat();
  const options = useGroupOptions("finance");
  const groupBy = query.data?.filters.groupBy ?? ctx.filters.groupBy ?? "day";
  const hidden = query.data?.costHidden ?? false;

  const columns = React.useMemo(
    () => [
      textCol<FinanceBucket>("label", tg(groupBy as "day"), (r) => <span className="font-medium text-strong">{r.label}</span>, { primary: true }),
      numCol<FinanceBucket>("orders", t("columns.orders"), (r) => f.int(r.orders), { hideOnMobile: true }),
      numCol<FinanceBucket>("gross", t("columns.grossSales"), (r) => f.money(r.grossSales), { hideOnMobile: true }),
      numCol<FinanceBucket>("discounts", t("columns.discounts"), (r) => f.money(r.discounts), { hideOnMobile: true }),
      numCol<FinanceBucket>("refunds", t("columns.refunds"), (r) => f.money(r.refunds), { hideOnMobile: true }),
      numCol<FinanceBucket>("net", t("columns.netRevenue"), (r) => <span className="font-medium">{f.money(r.netRevenue)}</span>),
      numCol<FinanceBucket>("delivery", t("columns.deliveryCollected"), (r) => f.money(r.deliveryCollected)),
      numCol<FinanceBucket>("courier", t("columns.courierCharges"), (r) => f.money(r.courierCharges), { hideOnMobile: true }),
      numCol<FinanceBucket>("fees", t("columns.gatewayFees"), () => <span className="text-muted">{t("notRecorded")}</span>, { hideOnMobile: true }),
      ...(hidden
        ? []
        : [
            numCol<FinanceBucket>("cost", t("columns.costOfGoods"), (r) => f.cost(r.costOfGoods), { hideOnMobile: true }),
            numCol<FinanceBucket>("profit", t("columns.grossProfit"), (r) => <span className="font-medium">{f.cost(r.grossProfit)}</span>),
          ]),
    ],
    [t, tg, groupBy, f, hidden],
  );

  return (
    <ReportFrame report="finance" ctx={ctx} query={query} groupOptions={options} groupLabel={t("groupLabel")} sections={SECTIONS.finance} flags={(d) => ({ costHidden: d.costHidden, gatewayFeesRecorded: false })}>
      {(d) => {
        const tot = d.totals;
        const points = shapeSeries(d.rows.map((r) => ({ label: r.label, netRevenue: r.netRevenue, deliveryCollected: r.deliveryCollected, grossProfit: r.grossProfit, orders: r.orders })));
        const series = [
          { key: "revenue", label: t("columns.netRevenue"), color: "var(--brand-primary)" },
          { key: "delivery", label: t("columns.deliveryCollected"), color: "var(--info)", dashed: true },
          ...(d.costHidden ? [] : [{ key: "profit", label: t("columns.grossProfit"), color: "var(--success)" }]),
        ];
        const best = points.reduce<(typeof points)[number] | null>((a, b) => (a === null || b.revenue > a.revenue ? b : a), null);
        const hiddenValue = <span className="text-base font-medium text-muted">{t("costHiddenValue")}</span>;
        return (
          <>
            <Tiles
              tiles={[
                { key: "gross", label: t("columns.grossSales"), value: f.money(tot.grossSales) },
                { key: "discounts", label: t("columns.discounts"), value: f.money(tot.discounts) },
                { key: "refunds", label: t("columns.refunds"), value: f.money(tot.refunds) },
                { key: "net", label: t("columns.netRevenue"), value: f.money(tot.netRevenue), emphasis: true },
                { key: "delivery", label: t("columns.deliveryCollected"), value: f.money(tot.deliveryCollected) },
                { key: "courier", label: t("columns.courierCharges"), value: f.money(tot.courierCharges) },
                { key: "balance", label: t("deliveryBalance"), value: f.money(d.deliveryBalance), hint: t("deliveryBalanceHint") },
                { key: "fees", label: t("columns.gatewayFees"), value: <span className="text-base font-medium text-muted">{t("notRecorded")}</span> },
                { key: "cost", label: t("columns.costOfGoods"), value: tot.costOfGoods === null ? hiddenValue : f.money(tot.costOfGoods) },
                { key: "profit", label: t("columns.grossProfit"), value: tot.grossProfit === null ? hiddenValue : f.money(tot.grossProfit), emphasis: true },
              ]}
            />
            <ChartCard
              title={t("chart.title")}
              description={t("chart.subtitle")}
              summary={best ? t("chart.summary", { count: points.length, from: f.dateOnly(d.filters.from), to: f.dateOnly(d.filters.to), best: best.label, amount: f.money(best.revenue) }) : t("chart.none")}
              legend={series}
              table={{
                caption: t("chart.tableCaption"),
                headers: [tg(groupBy as "day"), t("columns.netRevenue"), t("columns.deliveryCollected"), ...(d.costHidden ? [] : [t("columns.grossProfit")])],
                rows: points.map((p) => [p.label, f.money(p.revenue), f.money(p.delivery), ...(d.costHidden ? [] : [f.cost(p.profit)])]),
              }}
            >
              <TrendChart data={points} series={series} money={f.money} percent={f.pct} />
            </ChartCard>
            <Section title={t("cod.title")} description={t("cod.subtitle")} testId="finance-cod">
              <Tiles
                testId="cod-tiles"
                tiles={[
                  { key: "cod-collected", label: t("cod.collected"), value: f.money(d.cod.collected) },
                  { key: "cod-settled", label: t("cod.settled"), value: f.money(d.cod.settled) },
                  { key: "cod-pending", label: t("cod.pending"), value: f.money(d.cod.pendingWithCouriers), hint: t("cod.pendingHint") },
                  { key: "cod-transit", label: t("cod.inTransit"), value: f.money(d.cod.inTransit), hint: t("cod.inTransitHint") },
                ]}
              />
            </Section>
            <Section title={t("table.title")} description={t("table.subtitle", { count: d.rows.length, orders: f.int(d.rows.reduce((n, r) => n + num(r.orders), 0)) })} testId="finance-table">
              <ReportTable caption={t("table.title")} columns={columns} rows={d.rows} getRowId={(r, i) => `${r.start}-${i}`} />
            </Section>
          </>
        );
      }}
    </ReportFrame>
  );
}
