"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useSalesReport, type SalesRow } from "./api";
import { BarsChart, ChartCard, ReportFrame, ReportTable, Section, Tiles, useGroupOptions, useReportFilters, useReportFormat } from "./common";
import { shapeSales } from "./logic";
import { numCol, textCol } from "./table-kit";

/** Sales (FR-P01): totals as tiles, a chart of net sales, and every group as a table row. */
export function SalesView() {
  const t = useTranslations("reports.sales");
  const tg = useTranslations("reports.groupBy");
  const ctx = useReportFilters("sales");
  const query = useSalesReport(ctx.filters, ctx.ready);
  const f = useReportFormat();
  const options = useGroupOptions("sales");

  const data = query.data;
  const groupBy = data?.filters.groupBy ?? ctx.filters.groupBy ?? "day";
  const timed = groupBy === "day" || groupBy === "week" || groupBy === "month";

  const columns = React.useMemo(
    () => [
      textCol<SalesRow>("label", tg(groupBy as "day"), (r) => <span className="font-medium text-strong [overflow-wrap:anywhere]">{r.label}</span>, { primary: true }),
      numCol<SalesRow>("orders", t("columns.orders"), (r) => f.int(r.orders)),
      numCol<SalesRow>("units", t("columns.units"), (r) => f.int(r.units)),
      numCol<SalesRow>("gross", t("columns.gross"), (r) => f.money(r.gross), { hideOnMobile: true }),
      numCol<SalesRow>("discount", t("columns.discount"), (r) => f.money(r.discount), { hideOnMobile: true }),
      numCol<SalesRow>("net", t("columns.net"), (r) => f.money(r.net)),
      numCol<SalesRow>("delivery", t("columns.delivery"), (r) => f.money(r.delivery), { hideOnMobile: true }),
      numCol<SalesRow>("total", t("columns.total"), (r) => <span className="font-medium">{f.money(r.total)}</span>),
    ],
    [t, tg, groupBy, f],
  );

  return (
    <ReportFrame report="sales" ctx={ctx} query={query} groupOptions={options} groupLabel={t("groupLabel")} flags={(d) => ({ truncated: d.truncated })}>
      {(d) => {
        const chart = shapeSales(d.rows, groupBy);
        const best = chart.reduce<(typeof chart)[number] | null>((a, b) => (a === null || b.net > a.net ? b : a), null);
        const summary =
          chart.length === 0 || !best
            ? t("chart.none")
            : t("chart.summary", { count: chart.length, from: f.dateOnly(d.filters.from), to: f.dateOnly(d.filters.to), best: best.label, amount: f.money(best.net), total: f.money(d.totals.net) });
        const series = [{ key: "net", label: t("columns.net"), color: "var(--brand-primary)", kind: "bar" as const }];
        return (
          <>
            <Tiles
              tiles={[
                { key: "orders", label: t("columns.orders"), value: f.int(d.totals.orders) },
                { key: "units", label: t("columns.units"), value: f.int(d.totals.units) },
                { key: "gross", label: t("columns.gross"), value: f.money(d.totals.gross) },
                { key: "discount", label: t("columns.discount"), value: f.money(d.totals.discount) },
                { key: "net", label: t("columns.net"), value: f.money(d.totals.net) },
                { key: "delivery", label: t("columns.delivery"), value: f.money(d.totals.delivery) },
                { key: "total", label: t("columns.total"), value: f.money(d.totals.total), emphasis: true },
              ]}
            />
            <ChartCard
              title={t("chart.title")}
              description={timed ? t("chart.overTime") : t("chart.topGroups", { count: chart.length })}
              summary={summary}
              legend={series}
              table={{ caption: t("chart.tableCaption"), headers: [tg(groupBy as "day"), t("columns.orders"), t("columns.net")], rows: chart.map((c) => [c.label, c.orders, f.money(c.net)]) }}
            >
              <BarsChart data={chart} series={series} money={f.money} percent={f.pct} horizontal={!timed} />
            </ChartCard>
            <Section title={t("table.title")} description={!timed && groupBy !== "channel" && groupBy !== "zone" && groupBy !== "payment" ? t("table.lineNote") : undefined} testId="sales-table">
              <ReportTable caption={t("table.title")} columns={columns} rows={d.rows} getRowId={(r, i) => `${r.key}-${i}`} />
            </Section>
          </>
        );
      }}
    </ReportFrame>
  );
}
