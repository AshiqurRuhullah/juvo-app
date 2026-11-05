"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { Can } from "@/components/providers/session-provider";
import { useAllBranches, useAllRoles, useUsers, type UserListItem } from "./api";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "./shared";
import { UserCreateDialog } from "./user-create-dialog";

export function UsersList() {
  const t = useTranslations("ums.users");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const state = useListState<{ status?: string; roleId?: string; branchId?: string }>({}, 20, [{ id: "fullName", desc: false }]);
  const roles = useAllRoles();
  const branches = useAllBranches();

  const query = useUsers({
    page: state.page,
    pageSize: state.pageSize,
    sort: state.sort,
    search: state.search,
    isActive: state.filters.status === undefined ? undefined : state.filters.status === "active",
    roleId: state.filters.roleId,
    branchId: state.filters.branchId,
  });

  const columns = React.useMemo<ColumnDef<UserListItem, unknown>[]>(
    () => [
      {
        id: "fullName",
        header: t("columns.name"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/ums/users/${row.original.id}`} className="inline-flex min-h-11 flex-col justify-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="[overflow-wrap:anywhere]">{row.original.fullName}</span>
            <span className="text-sm font-normal text-muted [overflow-wrap:anywhere]">{row.original.email}</span>
          </Link>
        ),
      },
      {
        id: "roles",
        header: t("columns.roles"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.roles.length === 0 ? (
            <span className="text-muted">{t("noRoles")}</span>
          ) : (
            <span className="flex flex-wrap gap-1">
              {row.original.roles.slice(0, 3).map((role) => (
                <Badge key={role} variant="brand">
                  {role}
                </Badge>
              ))}
              {row.original.roles.length > 3 ? <Badge variant="outline">+{row.original.roles.length - 3}</Badge> : null}
            </span>
          ),
      },
      {
        id: "status",
        header: t("columns.status"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="flex flex-wrap items-center gap-1.5">
            {row.original.isActive ? <Badge variant="success">{t("active")}</Badge> : <Badge variant="outline">{t("inactive")}</Badge>}
            {row.original.isLockedOut ? <Badge variant="danger">{t("locked")}</Badge> : null}
            {row.original.isGlobalScope ? <Badge variant="info">{t("global")}</Badge> : null}
            {row.original.twoFactorEnabled ? (
              <span className="inline-flex items-center gap-1 text-sm text-muted" title={t("twoFactorOn")}>
                <ShieldCheck className="size-4 text-success" aria-hidden />
                <span className="sr-only">{t("twoFactorOn")}</span>
              </span>
            ) : null}
          </span>
        ),
      },
      {
        id: "lastLoginAt",
        header: t("columns.lastLogin"),
        meta: { hideOnMobile: true },
        cell: ({ row }) => <span className="text-muted">{row.original.lastLoginAt ? dateTime(row.original.lastLoginAt) : t("never")}</span>,
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/ums/users/${row.original.id}`} aria-label={t("open", { name: row.original.fullName })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, dateTime],
  );

  const filtersActive = !!(state.searchText || state.filters.status || state.filters.roleId || state.filters.branchId);

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
        <SearchBox className="lg:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <div className="grid flex-1 gap-3 sm:grid-cols-3">
          <FilterSelect
            value={state.filters.status}
            onChange={(v) => state.setFilter("status", v)}
            options={[
              { value: "active", label: t("active") },
              { value: "inactive", label: t("inactive") },
            ]}
            allLabel={t("filters.anyStatus")}
            label={t("filters.status")}
            searchPlaceholder={t("filters.search")}
            emptyText={t("filters.noMatch")}
          />
          <FilterSelect
            value={state.filters.roleId}
            onChange={(v) => state.setFilter("roleId", v)}
            options={(roles.data?.items ?? []).map((r) => ({ value: r.id, label: r.name }))}
            allLabel={t("filters.anyRole")}
            label={t("filters.role")}
            searchPlaceholder={t("filters.search")}
            emptyText={t("filters.noMatch")}
          />
          <FilterSelect
            value={state.filters.branchId}
            onChange={(v) => state.setFilter("branchId", v)}
            options={(branches.data?.items ?? []).map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` }))}
            allLabel={t("filters.anyBranch")}
            label={t("filters.branch")}
            searchPlaceholder={t("filters.search")}
            emptyText={t("filters.noMatch")}
          />
        </div>
        <div className="flex gap-2">
          {filtersActive ? (
            <Button variant="ghost" onClick={state.resetFilters}>
              {t("filters.clear")}
            </Button>
          ) : null}
          <Can permission="ums.user.create" scope="anywhere">
            <UserCreateDialog />
          </Can>
        </div>
      </div>

      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(u) => u.id}
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
