"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useCustomers, type CustomerListItem } from "./api";
import { ActiveBadge, LevelBadge, MaskedNotice, useCustomerRights } from "./common";
import { CUSTOMER_STATUSES, PROFILE_LEVELS, type CustomerFilters } from "./logic";

/** The staff customer list: search, status and profile-level filters, the table (cards on phones) and the shared pager. */
export function CustomersList() {
  const t = useTranslations("customers.list");
  const ts = useTranslations("customers.status");
  const tl = useTranslations("customers.level");
  const tc = useTranslations("customers.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const rights = useCustomerRights();
  const state = useListState<CustomerFilters>({});
  const query = useCustomers({ filters: state.filters, page: state.page, pageSize: state.pageSize, search: state.search });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const filtersActive = !!(state.searchText || Object.values(state.filters).some(Boolean));

  const columns = React.useMemo<ColumnDef<CustomerListItem, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/customers/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong [overflow-wrap:anywhere] hover:text-primary hover:underline" data-testid="customer-link">
            {row.original.fullName ?? <span className="font-normal text-muted">{t("noName")}</span>}
          </Link>
        ),
      },
      {
        id: "mobile",
        header: t("columns.mobile"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block [overflow-wrap:anywhere]" data-testid="customer-mobile">
            <span className="block font-mono text-sm">{row.original.mobile}</span>
            {row.original.email ? <span className="block text-xs text-muted">{row.original.email}</span> : null}
          </span>
        ),
      },
      { id: "level", header: t("columns.level"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <LevelBadge level={row.original.profileLevel} /> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <ActiveBadge isActive={row.original.isActive} /> },
      {
        id: "lastLogin",
        header: t("columns.lastLogin"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => <span className="text-muted">{row.original.lastLoginAt ? dateTime(row.original.lastLoginAt) : t("neverSignedIn")}</span>,
      },
      { id: "joined", header: t("columns.joined"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}</span> },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/customers/${row.original.id}`} aria-label={t("open", { name: row.original.fullName ?? row.original.mobile })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, dateTime],
  );

  return (
    <div className="space-y-4">
      <p className="text-muted">{t("subtitle")}</p>
      {!rights.viewPii ? <MaskedNotice where="list" /> : null}

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={rights.viewPii ? t("searchPlaceholder") : t("searchPlaceholderNameOnly")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={CUSTOMER_STATUSES.map((x) => ({ value: x, label: ts(x) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.level} onChange={(v) => state.setFilter("level", v)} options={PROFILE_LEVELS.map((x) => ({ value: x, label: tl(x) }))} allLabel={t("anyLevel")} label={t("columns.level")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>

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
