"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { InventoryReport } from "./api";
import { useInventoryReport } from "./api";
import { BarsChart, ChartCard, ReportFrame, ReportTable, Section, Tiles, useReportFilters, useReportFormat } from "./common";
import { SECTIONS, num, shapeStock } from "./logic";
import { numCol, textCol } from "./table-kit";

type Low = InventoryReport["lowStock"][number];
type Dead = InventoryReport["deadStock"][number];
type Move = InventoryReport["movements"][number];

/** Inventory (FR-P03): stock value at price and at cost, low stock, movements of the period and dead stock after N days. */
export function InventoryView() {
  const t = useTranslations("reports.inventory");
  const tr = useTranslations("inventory.reasons");
  const ctx = useReportFilters("plain");
  const query = useInventoryReport(ctx.filters, ctx.ready);
  const f = useReportFormat();
  const hidden = query.data?.costHidden ?? false;

  const low = React.useMemo(
    () => [
      textCol<Low>("product", t("columns.product"), (r) => <span className="[overflow-wrap:anywhere]"><span className="font-medium text-strong">{r.product}</span><span className="block text-sm text-muted">{r.sku}</span></span>, { primary: true }),
      textCol<Low>("branch", t("columns.branch"), (r) => <span className="font-mono text-sm">{r.branchCode}</span>),
      numCol<Low>("onHand", t("columns.onHand"), (r) => f.int(r.onHand)),
      numCol<Low>("reserved", t("columns.reserved"), (r) => f.int(r.reserved), { hideOnMobile: true }),
      numCol<Low>("available", t("columns.available"), (r) => f.int(r.available)),
      numCol<Low>("reorder", t("columns.reorderLevel"), (r) => f.int(r.reorderLevel)),
    ],
    [t, f],
  );
  const moves = React.useMemo(
    () => [
      textCol<Move>("reason", t("columns.reason"), (r) => <span className="font-medium text-strong">{tr.has(r.reason) ? tr(r.reason) : r.reason}</span>, { primary: true }),
      numCol<Move>("movements", t("columns.movements"), (r) => f.int(r.movements)),
      numCol<Move>("in", t("columns.in"), (r) => f.int(r.in)),
      numCol<Move>("out", t("columns.out"), (r) => f.int(r.out)),
      numCol<Move>("net", t("columns.net"), (r) => f.int(r.net)),
    ],
    [t, tr, f],
  );
  const dead = React.useMemo(
    () => [
      textCol<Dead>("product", t("columns.product"), (r) => <span className="[overflow-wrap:anywhere]"><span className="font-medium text-strong">{r.product}</span><span className="block text-sm text-muted">{r.sku}</span></span>, { primary: true }),
      numCol<Dead>("onHand", t("columns.onHand"), (r) => f.int(r.onHand)),
      numCol<Dead>("price", t("columns.valueAtPrice"), (r) => f.money(r.valueAtPrice)),
      ...(hidden ? [] : [numCol<Dead>("cost", t("columns.valueAtCost"), (r) => f.cost(r.valueAtCost), { hideOnMobile: true })]),
      textCol<Dead>("lastSold", t("columns.lastSold"), (r) => (r.lastSoldAt ? f.dateOnly(r.lastSoldAt) : <span className="text-muted">{t("neverSold")}</span>)),
    ],
    [t, f, hidden],
  );

  return (
    <ReportFrame report="inventory" ctx={ctx} query={query} showDays sections={SECTIONS.inventory} flags={(d) => ({ costHidden: d.costHidden })}>
      {(d) => {
        const s = d.summary;
        const branches = shapeStock(d.byBranch);
        const series = [
          { key: "price", label: t("columns.valueAtPrice"), color: "var(--brand-primary)" },
          ...(d.costHidden ? [] : [{ key: "cost", label: t("columns.valueAtCost"), color: "var(--info)" }]),
        ];
        const topBranch = branches.reduce<(typeof branches)[number] | null>((a, b) => (a === null || b.price > a.price ? b : a), null);
        return (
          <>
            <Tiles
              tiles={[
                { key: "variants", label: t("tiles.variants"), value: f.int(s.variants) },
                { key: "onHand", label: t("tiles.onHand"), value: f.int(s.onHand) },
                { key: "reserved", label: t("tiles.reserved"), value: f.int(s.reserved) },
                { key: "available", label: t("tiles.available"), value: f.int(s.available) },
                { key: "valuePrice", label: t("tiles.valueAtPrice"), value: f.money(s.valueAtPrice), emphasis: true },
                {
                  key: "valueCost",
                  label: t("tiles.valueAtCost"),
                  value: s.valueAtCost === null ? <span className="text-base font-medium text-muted">{t("costHiddenValue")}</span> : f.money(s.valueAtCost),
                  hint: !d.costHidden && num(s.unitsWithoutCost) > 0 ? t("tiles.withoutCost", { count: f.int(s.unitsWithoutCost) }) : undefined,
                  emphasis: true,
                },
                { key: "low", label: t("tiles.lowStock"), value: f.int(d.lowStockCount) },
                { key: "dead", label: t("tiles.deadStock"), value: f.int(d.deadStockCount), hint: t("tiles.deadValue", { amount: f.money(d.deadStockValueAtPrice) }) },
              ]}
            />
            {branches.length > 0 ? (
              <ChartCard
                title={t("chart.title")}
                description={t("chart.subtitle")}
                summary={topBranch ? t("chart.summary", { count: branches.length, branch: topBranch.label, amount: f.money(topBranch.price) }) : t("chart.none")}
                legend={series}
                table={{ caption: t("chart.tableCaption"), headers: [t("columns.branch"), t("columns.valueAtPrice"), ...(d.costHidden ? [] : [t("columns.valueAtCost")])], rows: branches.map((b) => [b.label, f.money(b.price), ...(d.costHidden ? [] : [f.cost(b.cost)])]) }}
                height="h-56 sm:h-64"
              >
                <BarsChart data={branches} series={series} money={f.money} percent={f.pct} />
              </ChartCard>
            ) : null}
            <Section title={t("low.title")} description={d.lowStock.length < num(d.lowStockCount) ? t("low.partial", { shown: f.int(d.lowStock.length), total: f.int(d.lowStockCount) }) : t("low.subtitle", { count: f.int(d.lowStockCount) })} testId="inventory-low">
              <ReportTable caption={t("low.title")} columns={low} rows={d.lowStock} getRowId={(r) => `${r.variantId}-${r.branchId}`} />
            </Section>
            <Section title={t("movements.title")} description={t("movements.subtitle")} testId="inventory-movements">
              <ReportTable caption={t("movements.title")} columns={moves} rows={d.movements} getRowId={(r) => r.reason} />
            </Section>
            <Section
              title={t("dead.title", { days: d.filters.days })}
              description={d.deadStock.length < num(d.deadStockCount) ? t("dead.partial", { shown: f.int(d.deadStock.length), total: f.int(d.deadStockCount) }) : t("dead.subtitle", { count: f.int(d.deadStockCount), amount: f.money(d.deadStockValueAtPrice) })}
              testId="inventory-dead"
            >
              <ReportTable caption={t("dead.title", { days: d.filters.days })} columns={dead} rows={d.deadStock} getRowId={(r) => r.variantId} />
            </Section>
          </>
        );
      }}
    </ReportFrame>
  );
}
