"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus, Settings } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCampaigns, useSegmentOptions, type CampaignListItem } from "./api";
import { CampaignStatusBadge, ChannelBadge, useMarketingPermissions } from "./common";
import { CAMPAIGN_STATUSES, MARKETING_CHANNELS, percent } from "./logic";

type Filters = { status?: string; channel?: string; segmentId?: string };

/** The campaign report (FR-AC04): every campaign with its status and results; the entry to the builder. */
export function CampaignsList() {
  const t = useTranslations("marketing.campaigns");
  const tc = useTranslations("marketing.common");
  const ts = useTranslations("marketing.status");
  const tch = useTranslations("marketing.channel");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const money = useMoney();
  const perms = useMarketingPermissions();
  const state = useListState<Filters>({});
  const segments = useSegmentOptions(perms.segmentView);
  const query = useCampaigns({ filters: state.filters, page: state.page, pageSize: state.pageSize, search: state.search });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const filtersActive = !!(state.searchText || Object.values(state.filters).some(Boolean));

  const columns = React.useMemo<ColumnDef<CampaignListItem, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/marketing/campaigns/${row.original.id}`} className="inline-flex min-h-11 max-w-xs items-center rounded-control font-semibold text-strong [overflow-wrap:anywhere] hover:text-primary hover:underline" data-testid="campaign-link">
            {row.original.name}
          </Link>
        ),
      },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <CampaignStatusBadge status={row.original.status} /> },
      { id: "channel", header: t("columns.channel"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <ChannelBadge channel={row.original.channel} /> },
      { id: "recipients", header: t("columns.recipients"), enableSorting: false, meta: { secondary: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.recipients)}</span> },
      {
        id: "sent",
        header: t("columns.sent"),
        enableSorting: false,
        meta: { secondary: true, numeric: true },
        cell: ({ row }) => (
          <span className="tabular-nums">
            {toNumber(row.original.sent)}
            <span className="ms-1 text-xs text-muted">({percent(toNumber(row.original.sent), toNumber(row.original.recipients))}%)</span>
          </span>
        ),
      },
      { id: "failed", header: t("columns.failed"), enableSorting: false, meta: { hideOnMobile: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.failed)}</span> },
      { id: "skipped", header: t("columns.skipped"), enableSorting: false, meta: { hideOnMobile: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.skipped)}</span> },
      { id: "unsubscribed", header: t("columns.unsubscribed"), enableSorting: false, meta: { hideOnMobile: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.unsubscribed)}</span> },
      { id: "orders", header: t("columns.orders"), enableSorting: false, meta: { secondary: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.attributedOrders)}</span> },
      { id: "revenue", header: t("columns.revenue"), enableSorting: false, meta: { secondary: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.attributedRevenue)}</span> },
      { id: "when", header: t("columns.when"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.startedAt ?? row.original.scheduledAt)}</span> },
    ],
    [t, money, dateTime],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="max-w-2xl text-muted">{t("subtitle")}</p>
        <div className="flex flex-wrap gap-2">
          {perms.campaignView ? (
            <Button asChild variant="secondary">
              <Link href="/admin/marketing/settings" data-testid="marketing-settings-link">
                <Settings aria-hidden /> {t("settings")}
              </Link>
            </Button>
          ) : null}
          {perms.campaignCreate ? (
            <Button asChild data-testid="new-campaign">
              <Link href="/admin/marketing/campaigns/new">
                <Plus aria-hidden /> {t("new")}
              </Link>
            </Button>
          ) : null}
        </div>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2 xl:col-span-1" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={CAMPAIGN_STATUSES.map((s) => ({ value: s, label: ts(s) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.channel} onChange={(v) => state.setFilter("channel", v)} options={MARKETING_CHANNELS.map((c) => ({ value: c, label: tch(c) }))} allLabel={t("anyChannel")} label={t("columns.channel")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {perms.segmentView ? (
          <FilterSelect value={state.filters.segmentId} onChange={(v) => state.setFilter("segmentId", v)} options={(segments.data?.items ?? []).map((s) => ({ value: s.id, label: s.name }))} allLabel={t("anySegment")} label={t("columns.segment")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>

      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
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
