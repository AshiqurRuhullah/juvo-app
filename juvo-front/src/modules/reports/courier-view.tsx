"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useCourierReport, type CourierRow } from "./api";
import { BarsChart, ChartCard, ReportFrame, ReportTable, Section, Tiles, useReportFilters, useReportFormat } from "./common";
import { shapeCourier } from "./logic";
import { numCol, textCol } from "./table-kit";

/** Courier performance (FR-P05): parcels and rates per courier, cost per parcel, and the cash collected against what was settled. */
export function CourierView() {
  const t = useTranslations("reports.courier");
  const ctx = useReportFilters("plain");
  const query = useCourierReport(ctx.filters, ctx.ready);
  const f = useReportFormat();

  const columns = React.useMemo(
    () => [
      textCol<CourierRow>("courier", t("columns.courier"), (r) => <span className="font-medium text-strong">{r.courier}</span>, { primary: true }),
      numCol<CourierRow>("parcels", t("columns.parcels"), (r) => f.int(r.parcels)),
      numCol<CourierRow>("delivered", t("columns.delivered"), (r) => `${f.int(r.delivered)} (${f.pct(r.deliveredRate)})`),
      numCol<CourierRow>("avg", t("columns.avgDelivery"), (r) => f.duration(r.avgDeliveryHours), { hideOnMobile: true }),
      numCol<CourierRow>("failed", t("columns.failed"), (r) => `${f.int(r.failed)} (${f.pct(r.failedRate)})`, { hideOnMobile: true }),
      numCol<CourierRow>("returned", t("columns.returned"), (r) => `${f.int(r.returned)} (${f.pct(r.returnedRate)})`, { hideOnMobile: true }),
      numCol<CourierRow>("cost", t("columns.totalCost"), (r) => f.money(r.totalCost), { hideOnMobile: true }),
      numCol<CourierRow>("perParcel", t("columns.costPerParcel"), (r) => f.money(r.costPerParcel)),
      numCol<CourierRow>("collected", t("columns.codCollected"), (r) => f.money(r.codCollected), { hideOnMobile: true }),
      numCol<CourierRow>("settled", t("columns.codSettled"), (r) => f.money(r.codSettled), { hideOnMobile: true }),
      numCol<CourierRow>("pending", t("columns.codPending"), (r) => <span className="font-medium">{f.money(r.codPending)}</span>),
    ],
    [t, f],
  );

  return (
    <ReportFrame report="courier" ctx={ctx} query={query}>
      {(d) => {
        const tot = d.totals;
        const rates = shapeCourier(d.rows);
        const series = [
          { key: "delivered", label: t("rates.delivered"), color: "var(--success)", percent: true },
          { key: "failed", label: t("rates.failed"), color: "var(--warning)", percent: true },
          { key: "returned", label: t("rates.returned"), color: "var(--danger)", percent: true },
        ];
        return (
          <>
            <Tiles
              tiles={[
                { key: "parcels", label: t("columns.parcels"), value: f.int(tot.parcels), emphasis: true },
                { key: "delivered", label: t("columns.delivered"), value: f.pct(tot.deliveredRate), hint: t("tiles.ofParcels", { count: f.int(tot.delivered) }) },
                { key: "failed", label: t("columns.failed"), value: f.pct(tot.failedRate), hint: t("tiles.ofParcels", { count: f.int(tot.failed) }) },
                { key: "returned", label: t("columns.returned"), value: f.pct(tot.returnedRate), hint: t("tiles.ofParcels", { count: f.int(tot.returned) }) },
                { key: "avg", label: t("columns.avgDelivery"), value: f.duration(tot.avgDeliveryHours) },
                { key: "cost", label: t("columns.totalCost"), value: f.money(tot.totalCost), hint: t("tiles.perParcel", { amount: f.money(tot.costPerParcel) }) },
                { key: "collected", label: t("columns.codCollected"), value: f.money(tot.codCollected) },
                { key: "pending", label: t("columns.codPending"), value: f.money(tot.codPending), hint: t("tiles.settled", { amount: f.money(tot.codSettled) }) },
              ]}
            />
            {rates.length > 0 ? (
              <ChartCard
                title={t("chart.title")}
                description={t("chart.subtitle")}
                summary={t("chart.summary", { count: rates.length, rate: f.pct(tot.deliveredRate) })}
                legend={series}
                table={{ caption: t("chart.tableCaption"), headers: [t("columns.courier"), t("rates.delivered"), t("rates.failed"), t("rates.returned")], rows: rates.map((r) => [r.label, f.pct(r.delivered), f.pct(r.failed), f.pct(r.returned)]) }}
                height="h-56 sm:h-64"
              >
                <BarsChart data={rates} series={series} money={f.money} percent={f.pct} horizontal />
              </ChartCard>
            ) : null}
            <Section title={t("table.title")} description={t("table.subtitle")} testId="courier-table">
              <ReportTable caption={t("table.title")} columns={columns} rows={d.rows} getRowId={(r) => r.courierId} />
            </Section>
          </>
        );
      }}
    </ReportFrame>
  );
}
