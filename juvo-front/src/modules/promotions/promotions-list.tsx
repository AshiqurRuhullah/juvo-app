"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Tabs } from "@/components/ui/tabs";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCampaignTotals, usePromotions, type PromotionListItem } from "./api";
import { StatusBadge, usePromotionPermissions, useStatusOptions, useTypeLabel, useTypeOptions } from "./common";
import { summarizeReport, usageText, type PromotionFilters } from "./logic";

const CampaignReport = dynamic(() => import("./report-view"), { ssr: false, loading: () => <Skeleton className="h-64" aria-busy="true" /> });

/** `/admin/promotions`: the list and the campaign report as two tabs. */
export function PromotionsHome() {
  const t = useTranslations("promotions.tabs");
  const [tab, setTab] = React.useState("list");
  const tabs = [
    { id: "list", label: t("list") },
    { id: "report", label: t("report") },
  ];
  return (
    <Tabs tabs={tabs} value={tab} onValueChange={setTab} label={t("label")} className="space-y-4">
      {(active) => (active === "list" ? <PromotionsList /> : <CampaignReport />)}
    </Tabs>
  );
}

/** Totals over every promotion, from the report endpoint. */
function SummaryStrip() {
  const t = useTranslations("promotions.summary");
  const money = useMoney();
  const query = useCampaignTotals();
  if (query.isPending) return <Skeleton className="h-20" aria-busy="true" />;
  if (query.isError || !query.data) return <Alert variant="danger">{t("failed")}</Alert>;
  const s = summarizeReport(query.data.rows);
  const cells: { key: string; value: string }[] = [
    { key: "promotions", value: String(toNumber(query.data.total)) },
    { key: "orders", value: String(s.orders) },
    { key: "units", value: String(s.units) },
    { key: "revenue", value: money(s.revenue) },
    { key: "discountCost", value: money(s.discountCost) },
  ];
  return (
    <section aria-label={t("label")} data-testid="promotion-summary" className="space-y-1">
      <ul className="grid grid-cols-2 gap-2 md:grid-cols-5">
        {cells.map((c) => (
          <li key={c.key} className="rounded-card border border-line bg-card p-3" data-testid={`summary-${c.key}`}>
            <span className="block text-xl font-bold tabular-nums text-strong">{c.value}</span>
            <span className="block text-sm text-muted">{t(c.key)}</span>
          </li>
        ))}
      </ul>
      <p className="text-sm text-muted">{t("note")}</p>
    </section>
  );
}

export function PromotionsList() {
  const t = useTranslations("promotions.list");
  const tc = useTranslations("promotions.common");
  const typeLabel = useTypeLabel();
  const typeOptions = useTypeOptions();
  const statusOptions = useStatusOptions();
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const perms = usePromotionPermissions();
  const state = useListState<PromotionFilters>({});
  const query = usePromotions({ page: state.page, pageSize: state.pageSize, search: state.search, type: state.filters.type, status: state.filters.status });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const filtersActive = !!(state.searchText || state.filters.type || state.filters.status);

  const columns = React.useMemo<ColumnDef<PromotionListItem, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block max-w-xs [overflow-wrap:anywhere]">
            <Link href={`/admin/promotions/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline" data-testid="promotion-link">
              {row.original.nameEn}
            </Link>
            {row.original.slug ? <span className="block font-mono text-xs text-muted">/{row.original.slug}</span> : null}
          </span>
        ),
      },
      { id: "type", header: t("columns.type"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span>{typeLabel(row.original.type)}</span> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <StatusBadge status={row.original.status} /> },
      {
        id: "schedule",
        header: t("columns.schedule"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block text-sm text-muted">
            {row.original.startsAt || row.original.endsAt ? (
              <>
                <span className="block">{row.original.startsAt ? dateTime(row.original.startsAt) : t("noStart")}</span>
                <span className="block">{row.original.endsAt ? dateTime(row.original.endsAt) : t("noEnd")}</span>
              </>
            ) : (
              t("always")
            )}
          </span>
        ),
      },
      { id: "priority", header: t("columns.priority"), enableSorting: false, meta: { hideOnMobile: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.priority)}</span> },
      {
        id: "used",
        header: t("columns.used"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => {
          const u = usageText(row.original.usedCount, row.original.usageLimit);
          return <span className="tabular-nums">{u.limit === null ? t("usedUnlimited", { used: u.used }) : t("usedOf", { used: u.used, limit: u.limit })}</span>;
        },
      },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/promotions/${row.original.id}`} aria-label={t("open", { name: row.original.nameEn })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, typeLabel, dateTime],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-muted">{t("subtitle")}</p>
        {perms.create ? (
          <Button asChild data-testid="new-promotion">
            <Link href="/admin/promotions/new">
              <Plus aria-hidden /> {t("new")}
            </Link>
          </Button>
        ) : null}
      </div>

      <SummaryStrip />

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2 xl:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.type} onChange={(v) => state.setFilter("type", v)} options={typeOptions} allLabel={t("anyType")} label={t("columns.type")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={statusOptions} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>
      <p className="text-sm text-muted">{t("channelNote")}</p>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={items}
        loading={query.isLoading}
        getRowId={(x) => x.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
      />
    </div>
  );
}
