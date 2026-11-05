"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Input } from "@/components/ui/form";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert } from "@/components/ui/surfaces";
import { useSignInHistory } from "./api";
import { FilterSelect, SearchBox, useListState, useTableLabels } from "./shared";
import { useSignInColumns } from "./user-sessions";

const dayStart = (date: string) => (date ? new Date(`${date}T00:00:00`).toISOString() : undefined);
const dayEnd = (date: string) => (date ? new Date(`${date}T23:59:59.999`).toISOString() : undefined);

/** Every sign-in attempt, successful or not (staff and customers), newest first. */
export function SignInViewer() {
  const t = useTranslations("ums.signIns");
  const labels = useTableLabels();
  const state = useListState<{ result?: string; subjectType?: string; from?: string; to?: string }>({}, 25);
  const columns = useSignInColumns({ showIdentifier: true });

  const query = useSignInHistory({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    succeeded: state.filters.result === undefined ? undefined : state.filters.result === "succeeded",
    subjectType: state.filters.subjectType,
    from: dayStart(state.filters.from ?? ""),
    to: dayEnd(state.filters.to ?? ""),
  });

  const filtersActive = !!(state.searchText || state.filters.result || state.filters.subjectType || state.filters.from || state.filters.to);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-5">
        <SearchBox className="xl:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect
          value={state.filters.result}
          onChange={(v) => state.setFilter("result", v)}
          options={[
            { value: "succeeded", label: t("succeeded") },
            { value: "failed", label: t("failed") },
          ]}
          allLabel={t("filters.anyResult")}
          label={t("result")}
          searchPlaceholder={t("filters.search")}
          emptyText={t("filters.noMatch")}
        />
        <FilterSelect
          value={state.filters.subjectType}
          onChange={(v) => state.setFilter("subjectType", v)}
          options={[
            { value: "Staff", label: t("staff") },
            { value: "Customer", label: t("customer") },
          ]}
          allLabel={t("filters.anySubject")}
          label={t("filters.subject")}
          searchPlaceholder={t("filters.search")}
          emptyText={t("filters.noMatch")}
        />
        <div className="grid grid-cols-2 gap-3">
          <Input type="date" value={state.filters.from ?? ""} onChange={(e) => state.setFilter("from", e.target.value || undefined)} aria-label={t("filters.from")} />
          <Input type="date" value={state.filters.to ?? ""} onChange={(e) => state.setFilter("to", e.target.value || undefined)} aria-label={t("filters.to")} />
        </div>
      </div>
      {filtersActive ? (
        <Button variant="ghost" onClick={state.resetFilters}>
          {t("filters.clear")}
        </Button>
      ) : null}
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("pageTitle")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(s) => s.id}
        labels={{ ...labels, empty: t("empty"), emptyDescription: undefined }}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
      />
    </div>
  );
}
