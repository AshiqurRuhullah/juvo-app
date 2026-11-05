"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Copy, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { Can, usePermissions } from "@/components/providers/session-provider";
import { useRoles, type RoleListItem } from "./api";
import { CloneRoleDialog } from "./role-clone-dialog";
import { SearchBox, useListState, useTableLabels } from "./shared";

export function RolesList() {
  const t = useTranslations("ums.roles");
  const labels = useTableLabels();
  const { can } = usePermissions();
  const state = useListState<Record<string, never>>({}, 20, [{ id: "name", desc: false }]);
  const query = useRoles({ page: state.page, pageSize: state.pageSize, sort: state.sort, search: state.search });
  const canClone = can("ums.role.clone", "anywhere");

  const columns = React.useMemo<ColumnDef<RoleListItem, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/ums/roles/${row.original.id}`} className="inline-flex min-h-11 flex-col justify-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="[overflow-wrap:anywhere]">{row.original.name}</span>
            {row.original.description ? <span className="text-sm font-normal text-muted [overflow-wrap:anywhere]">{row.original.description}</span> : null}
          </Link>
        ),
      },
      {
        id: "type",
        header: t("columns.type"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.isSuperAdmin ? <Badge variant="danger">{t("superAdmin")}</Badge> : row.original.isSystem ? <Badge variant="info">{t("system")}</Badge> : <Badge variant="outline">{t("custom")}</Badge>,
      },
      { id: "users", header: t("columns.users"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => t("users", { count: row.original.userCount }) },
      { id: "permissions", header: t("columns.permissions"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => (row.original.isSuperAdmin ? t("all") : row.original.permissionCount) },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <span className="flex flex-wrap justify-end gap-2">
            {canClone ? (
              <CloneRoleDialog source={row.original}>
                <Button variant="ghost" aria-label={t("cloneNamed", { name: row.original.name })}>
                  <Copy aria-hidden /> <span className="hidden lg:inline">{t("clone")}</span>
                </Button>
              </CloneRoleDialog>
            ) : null}
            <Button asChild variant="secondary">
              <Link href={`/admin/ums/roles/${row.original.id}`} aria-label={t("open", { name: row.original.name })}>
                {t("view")}
              </Link>
            </Button>
          </span>
        ),
      },
    ],
    [t, canClone],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <SearchBox className="sm:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <Can permission="ums.role.create" scope="anywhere">
          <Button asChild>
            <Link href="/admin/ums/roles/new">
              <Plus aria-hidden /> {t("new")}
            </Link>
          </Button>
        </Can>
      </div>
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(r) => r.id}
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
