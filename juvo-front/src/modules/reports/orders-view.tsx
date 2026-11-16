"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { TrendingDown } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { useOrdersReport } from "./api";
import { ReportFrame, ReportTable, Section, Tiles, useReportFilters, useReportFormat } from "./common";
import { SECTIONS, num, shapeFunnel } from "./logic";
import { numCol, textCol } from "./table-kit";

type Reason = { code: string; label: string; count: number | string; share: number | string; fault: string | null };

/** Orders (FR-P02): the funnel with its rates, then why orders were cancelled and returned. */
export function OrdersView() {
  const t = useTranslations("reports.orders");
  const ts = useTranslations("reports.stages");
  const tf = useTranslations("reports.fault");
  const ctx = useReportFilters("plain");
  const query = useOrdersReport(ctx.filters, ctx.ready);
  const f = useReportFormat();

  const columns = React.useMemo(
    () => [
      textCol<Reason>("label", t("reasons.reason"), (r) => <span className="font-medium text-strong [overflow-wrap:anywhere]">{r.label}</span>, { primary: true }),
      numCol<Reason>("count", t("reasons.count"), (r) => f.int(r.count)),
      numCol<Reason>("share", t("reasons.share"), (r) => f.pct(r.share)),
      textCol<Reason>("fault", t("reasons.fault"), (r) => (r.fault ? <Badge variant="outline">{tf.has(r.fault) ? tf(r.fault) : r.fault}</Badge> : <span className="text-muted">-</span>)),
    ],
    [t, tf, f],
  );

  return (
    <ReportFrame report="orders" ctx={ctx} query={query} sections={SECTIONS.orders}>
      {(d) => {
        const funnel = shapeFunnel(d.funnel);
        const delivered = funnel.main.find((s) => s.stage === "Delivered");
        const summary = t("funnel.summary", { placed: f.int(d.placed), delivered: f.int(delivered?.count ?? 0), rate: f.pct(delivered?.rateOfPlaced ?? 0) });
        return (
          <>
            <Tiles
              tiles={[
                { key: "placed", label: t("tiles.placed"), value: f.int(d.placed), emphasis: true },
                { key: "delivered", label: t("tiles.delivered"), value: f.pct(delivered?.rateOfPlaced ?? 0), hint: t("tiles.ofPlaced") },
                { key: "cancelled", label: t("tiles.cancelled"), value: f.pct(d.cancelledRate), hint: t("tiles.ofPlaced") },
                { key: "returned", label: t("tiles.returned"), value: f.pct(d.returnedRate), hint: t("tiles.ofPlaced") },
              ]}
            />
            <Section title={t("funnel.title")} description={t("funnel.subtitle")} testId="orders-funnel">
              <p className="mb-3 text-sm text-muted" data-testid="chart-summary">
                {summary}
              </p>
              <ol className="space-y-3" aria-label={t("funnel.title")}>
                {funnel.main.map((s) => (
                  <li key={s.stage} data-testid={`funnel-${s.stage}`}>
                    <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-0.5">
                      <span className="font-medium text-strong">{ts(s.stage)}</span>
                      <span className="text-sm tabular-nums text-muted">
                        <span className="font-semibold text-strong" data-testid="funnel-count">{f.int(s.count)}</span> · {t("funnel.ofPlaced", { rate: f.pct(s.rateOfPlaced) })}
                        {s.stage !== "Placed" ? ` · ${t("funnel.ofPrevious", { rate: f.pct(s.rateOfPrevious) })}` : ""}
                      </span>
                    </div>
                    <div className="mt-1.5 h-3 overflow-hidden rounded-full bg-sunken" aria-hidden>
                      <div className="h-full rounded-full bg-primary/70" style={{ width: `${s.width}%` }} />
                    </div>
                  </li>
                ))}
              </ol>
              <ul className="mt-5 grid gap-3 sm:grid-cols-2">
                {funnel.losses.map((s) => (
                  <li key={s.stage} className="flex items-center justify-between gap-3 rounded-control border border-line p-3" data-testid={`funnel-${s.stage}`}>
                    <span className="flex items-center gap-2 font-medium text-strong">
                      <TrendingDown className="size-4 text-danger-ink" aria-hidden /> {ts(s.stage)}
                    </span>
                    <span className="text-sm tabular-nums text-muted">
                      <span className="font-semibold text-strong" data-testid="funnel-count">{f.int(s.count)}</span> · {f.pct(s.rateOfPlaced)}
                    </span>
                  </li>
                ))}
              </ul>
            </Section>
            <div className="grid grid-cols-[minmax(0,1fr)] gap-4 xl:grid-cols-2">
              <Section title={t("cancellations.title")} description={t("cancellations.subtitle", { count: d.cancellationReasons.reduce((n, r) => n + num(r.count), 0) })} testId="orders-cancellations">
                <ReportTable caption={t("cancellations.title")} columns={columns} rows={d.cancellationReasons} getRowId={(r) => r.code} />
              </Section>
              <Section title={t("returnsTable.title")} description={t("returnsTable.subtitle", { count: d.returnReasons.reduce((n, r) => n + num(r.count), 0) })} testId="orders-returns">
                <ReportTable caption={t("returnsTable.title")} columns={columns} rows={d.returnReasons} getRowId={(r) => r.code} />
              </Section>
            </div>
          </>
        );
      }}
    </ReportFrame>
  );
}
