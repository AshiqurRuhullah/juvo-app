"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { CustomersReport } from "./api";
import { useCustomersReport } from "./api";
import { BarsChart, ChartCard, ReportFrame, ReportTable, Section, Tiles, useReportFilters, useReportFormat } from "./common";
import { SECTIONS, num } from "./logic";
import { numCol, textCol } from "./table-kit";

type Spender = CustomersReport["topSpenders"][number];

/** Customers (FR-P06): new against returning, how many guests became profiles, and the biggest spenders. */
export function CustomersView() {
  const t = useTranslations("reports.customers");
  const ctx = useReportFilters("plain");
  const query = useCustomersReport(ctx.filters, ctx.ready);
  const f = useReportFormat();

  const columns = React.useMemo(
    () => [
      textCol<Spender>("name", t("columns.customer"), (r) => <span className="[overflow-wrap:anywhere]"><span className="font-medium text-strong">{r.name ?? t("noName")}</span><span className="block text-sm text-muted tabular-nums">{r.mobile}</span></span>, { primary: true }),
      numCol<Spender>("orders", t("columns.orders"), (r) => f.int(r.orders)),
      numCol<Spender>("spent", t("columns.spent"), (r) => <span className="font-medium">{f.money(r.spent)}</span>),
    ],
    [t, f],
  );

  return (
    <ReportFrame report="customers" ctx={ctx} query={query} sections={SECTIONS.customers}>
      {(d) => {
        const groups = [
          { label: t("newCustomers"), customers: num(d.newCustomers), orders: num(d.ordersFromNew), revenue: num(d.revenueFromNew) },
          { label: t("returningCustomers"), customers: num(d.returningCustomers), orders: num(d.ordersFromReturning), revenue: num(d.revenueFromReturning) },
        ];
        const series = [
          { key: "customers", label: t("columns.customers"), color: "var(--brand-primary)" },
          { key: "orders", label: t("columns.orders"), color: "var(--info)" },
        ];
        return (
          <>
            <Tiles
              tiles={[
                { key: "customers", label: t("columns.customers"), value: f.int(d.customers), emphasis: true },
                { key: "new", label: t("newCustomers"), value: f.int(d.newCustomers) },
                { key: "returning", label: t("returningCustomers"), value: f.int(d.returningCustomers) },
                { key: "revenue-new", label: t("revenueFromNew"), value: f.money(d.revenueFromNew) },
                { key: "revenue-returning", label: t("revenueFromReturning"), value: f.money(d.revenueFromReturning) },
                { key: "guests", label: t("guestsCreated"), value: f.int(d.guestsCreated) },
                { key: "converted", label: t("convertedToProfile"), value: f.int(d.convertedToProfile) },
                { key: "conversion", label: t("conversionRate"), value: f.pct(d.conversionRate), emphasis: true },
              ]}
            />
            <ChartCard
              title={t("chart.title")}
              description={t("chart.subtitle")}
              summary={t("chart.summary", { newCount: f.int(d.newCustomers), returning: f.int(d.returningCustomers), newOrders: f.int(d.ordersFromNew), returningOrders: f.int(d.ordersFromReturning) })}
              legend={series}
              table={{ caption: t("chart.tableCaption"), headers: [t("columns.group"), t("columns.customers"), t("columns.orders"), t("revenue")], rows: groups.map((g) => [g.label, g.customers, g.orders, f.money(g.revenue)]) }}
              height="h-52 sm:h-60"
            >
              <BarsChart data={groups} series={series} money={f.int} percent={f.pct} />
            </ChartCard>
            <Section title={t("top.title")} description={t("top.subtitle")} testId="customers-top">
              <ReportTable caption={t("top.title")} columns={columns} rows={d.topSpenders} getRowId={(r) => r.customerId} />
            </Section>
          </>
        );
      }}
    </ReportFrame>
  );
}
