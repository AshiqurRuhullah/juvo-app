"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { Can } from "@/components/providers/session-provider";
import { useBranches, type BranchDto } from "./api";
import { FilterSelect, SearchBox, useListState, useTableLabels } from "./shared";

export function BranchesList() {
  const t = useTranslations("ums.branches");
  const labels = useTableLabels();
  const state = useListState<{ status?: string }>({}, 20, [{ id: "name", desc: false }]);
  const query = useBranches({
    page: state.page,
    pageSize: state.pageSize,
    sort: state.sort,
    search: state.search,
    isActive: state.filters.status === undefined ? undefined : state.filters.status === "active",
  });

  const columns = React.useMemo<ColumnDef<BranchDto, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/ums/branches/${row.original.id}`} className="inline-flex min-h-11 flex-col justify-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="[overflow-wrap:anywhere]">{row.original.nameEn}</span>
            {row.original.nameBn ? (
              <span lang="bn" className="text-sm font-normal text-muted [overflow-wrap:anywhere]">
                {row.original.nameBn}
              </span>
            ) : null}
          </Link>
        ),
      },
      { id: "code", header: t("columns.code"), meta: { secondary: true }, cell: ({ row }) => <code className="rounded-md bg-sunken px-1.5 py-0.5 text-sm">{row.original.code}</code> },
      { id: "type", header: t("columns.type"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => t(`types.${row.original.type}`) },
      {
        id: "status",
        header: t("columns.status"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="flex flex-wrap gap-1.5">
            {row.original.isActive ? <Badge variant="success">{t("active")}</Badge> : <Badge variant="outline">{t("inactive")}</Badge>}
            {row.original.isDefault ? <Badge variant="brand">{t("default")}</Badge> : null}
          </span>
        ),
      },
      { id: "users", header: t("columns.users"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => row.original.userCount },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/ums/branches/${row.original.id}`} aria-label={t("open", { name: row.original.nameEn })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
        <SearchBox className="lg:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <div className="sm:w-56">
          <FilterSelect
            value={state.filters.status}
            onChange={(v) => state.setFilter("status", v)}
            options={[
              { value: "active", label: t("active") },
              { value: "inactive", label: t("inactive") },
            ]}
            allLabel={t("anyStatus")}
            label={t("status")}
            searchPlaceholder={t("search")}
            emptyText={t("noMatch")}
          />
        </div>
        <div className="lg:ms-auto">
          <Can permission="ums.branch.create" scope="anywhere">
            <Button asChild>
              <Link href="/admin/ums/branches/new">
                <Plus aria-hidden /> {t("new")}
              </Link>
            </Button>
          </Can>
        </div>
      </div>
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(b) => b.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
        sorting={state.sorting}
        onSortingChange={state.setSorting}
      />
    </div>
  );
}
