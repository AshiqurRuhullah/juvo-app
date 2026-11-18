"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Info } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { FilterSelect, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCampaignReport, usePromotionReport, type PromotionReport } from "./api";
import { StatusBadge, useTypeLabel, useTypeOptions } from "./common";

type Filters = { type?: string; from?: string; to?: string };

/** The honest limitation of the report: numbers come from the order lines and are not reduced by returns (docs/promotions-api.md). */
export function ReturnsNote() {
  const t = useTranslations("promotions.report");
  return (
    <Alert variant="info" data-testid="report-returns-note">
      <span className="flex items-start gap-2">
        <Info className="mt-0.5 size-4 shrink-0" aria-hidden />
        <span>{t("returnsNote")}</span>
      </span>
    </Alert>
  );
}

/** `/admin/promotions` report tab: one row per promotion for an optional period (Dhaka days). */
export default function CampaignReport() {
  const t = useTranslations("promotions.report");
  const tc = useTranslations("promotions.common");
  const labels = useTableLabels();
  const money = useMoney();
  const typeLabel = useTypeLabel();
  const typeOptions = useTypeOptions();
  const state = useListState<Filters>({});
  const query = useCampaignReport({ page: state.page, pageSize: state.pageSize, type: state.filters.type, from: state.filters.from, to: state.filters.to });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const filtersActive = !!(state.filters.type || state.filters.from || state.filters.to);

  const columns = React.useMemo<ColumnDef<PromotionReport, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.promotion"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/promotions/${row.original.promotionId}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong [overflow-wrap:anywhere] hover:text-primary hover:underline" data-testid="report-link">
            {row.original.nameEn}
          </Link>
        ),
      },
      { id: "type", header: t("columns.type"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span>{typeLabel(row.original.type)}</span> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <StatusBadge status={row.original.status} /> },
      { id: "orders", header: t("columns.orders"), enableSorting: false, meta: { secondary: true, numeric: true, label: t("columns.orders") }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.orders)}</span> },
      { id: "units", header: t("columns.units"), enableSorting: false, meta: { secondary: true, numeric: true, label: t("columns.units") }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.units)}</span> },
      { id: "revenue", header: t("columns.revenue"), enableSorting: false, meta: { secondary: true, numeric: true, label: t("columns.revenue") }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.revenue)}</span> },
      { id: "discountCost", header: t("columns.discountCost"), enableSorting: false, meta: { secondary: true, numeric: true, label: t("columns.discountCost") }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.discountCost)}</span> },
    ],
    [t, typeLabel, money],
  );

  return (
    <div className="space-y-4" data-testid="campaign-report">
      <p className="text-muted">{t("subtitle")}</p>
      <ReturnsNote />
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <FilterSelect value={state.filters.type} onChange={(v) => state.setFilter("type", v)} options={typeOptions} allLabel={t("anyType")} label={t("columns.type")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <Field label={t("from")} htmlFor="rp-from">
          <Input id="rp-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="rp-to">
          <Input id="rp-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>
      <p className="text-sm text-muted">{t("periodNote")}</p>
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => x.promotionId} labels={labels} page={state.page} pageSize={state.pageSize} total={toNumber(query.data?.total)} onPageChange={state.setPage} />
    </div>
  );
}

/** The results of one promotion (on its own page): four figures for an optional period, and the returns limitation. */
export function PromotionResults({ id }: { id: string }) {
  const t = useTranslations("promotions.report");
  const money = useMoney();
  const [from, setFrom] = React.useState("");
  const [to, setTo] = React.useState("");
  const query = usePromotionReport(id, from || undefined, to || undefined);
  const r = query.data;
  const cells = r
    ? [
        { key: "orders", value: String(toNumber(r.orders)) },
        { key: "units", value: String(toNumber(r.units)) },
        { key: "revenue", value: money(r.revenue) },
        { key: "discountCost", value: money(r.discountCost) },
      ]
    : [];
  return (
    <section className="space-y-3" aria-labelledby="results-title" data-testid="promotion-results">
      <h2 id="results-title" className="font-display text-xl font-semibold text-strong">
        {t("oneTitle")}
      </h2>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Field label={t("from")} htmlFor="one-from">
          <Input id="one-from" type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label={t("to")} htmlFor="one-to">
          <Input id="one-to" type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} />
        </Field>
      </div>
      {query.isPending ? <Skeleton className="h-20" aria-busy="true" /> : null}
      {query.isError ? <Alert variant="danger">{t("failed")}</Alert> : null}
      {r ? (
        <ul className="grid grid-cols-2 gap-2 md:grid-cols-4">
          {cells.map((c) => (
            <li key={c.key} className="rounded-card border border-line bg-card p-3" data-testid={`result-${c.key}`}>
              <span className="block text-xl font-bold tabular-nums text-strong">{c.value}</span>
              <span className="block text-sm text-muted">{t(`columns.${c.key}`)}</span>
            </li>
          ))}
        </ul>
      ) : null}
      <ReturnsNote />
    </section>
  );
}
