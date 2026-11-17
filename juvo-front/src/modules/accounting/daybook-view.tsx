"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import { FilterSelect, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { Stat } from "@/modules/purchasing/common";
import { useAccounts, useDaybook, type DaybookEntry } from "./api";
import { Flow, useDateOnly, useMoney2 } from "./common";
import { netOf } from "./logic";

type Filters = { from?: string; to?: string; branchId?: string; accountId?: string };

/**
 * The day book: cash book lines plus money read from the payment data (customer receipts, settled COD cash, refunds), newest first.
 * The strip shows the API's totals for the whole range; "this page" is the net of the rows shown, so a page can be checked by hand.
 */
export function DaybookView() {
  const t = useTranslations("accounting.daybook");
  const tk = useTranslations("accounting.kind");
  const tc = useTranslations("accounting.common");
  const labels = useTableLabels();
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const branches = useBranchOptions();
  const state = useListState<Filters>({});
  const accounts = useAccounts({ includeInactive: true });

  const query = useDaybook({ page: state.page, pageSize: state.pageSize, ...state.filters });
  const d = query.data;
  const items = React.useMemo(() => d?.entries.items ?? [], [d?.entries.items]);
  const total = toNumber(d?.entries.total);
  const pageNet = React.useMemo(() => netOf(items.map((i) => toNumber(i.amount))), [items]);
  const accountOptions = React.useMemo(() => (accounts.data ?? []).map((a) => ({ value: a.id, label: `${a.branchCode} · ${a.name}` })), [accounts.data]);

  const columns = React.useMemo<ColumnDef<DaybookEntry, unknown>[]>(
    () => [
      { id: "date", header: t("columns.date"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="whitespace-nowrap">{dateOnly(row.original.date)}</span> },
      {
        id: "description",
        header: t("columns.description"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block [overflow-wrap:anywhere]">
            {row.original.description}
            <span className="mt-0.5 flex flex-wrap items-center gap-1.5 text-sm text-muted">
              {tk.has(row.original.kind) ? tk(row.original.kind) : row.original.kind}
              {row.original.derived ? <Badge variant="outline">{t("fromPayments")}</Badge> : null}
            </span>
          </span>
        ),
      },
      { id: "account", header: t("columns.account"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span>{row.original.accountName ?? "-"}</span> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "amount", header: t("columns.amount"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <Flow amount={toNumber(row.original.amount)} format={money} /> },
    ],
    [t, tk, dateOnly, money],
  );

  const filtersActive = Object.values(state.filters).some(Boolean);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Field label={tc("from")} htmlFor="db-from" hint={d ? undefined : t("rangeHint")}>
          <Input id="db-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor="db-to">
          <Input id="db-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
        {branches.length > 1 ? (
          <div className="self-end">
            <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </div>
        ) : null}
        <div className="self-end">
          <FilterSelect value={state.filters.accountId} onChange={(v) => state.setFilter("accountId", v)} options={accountOptions} allLabel={t("anyAccount")} label={t("columns.account")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </div>
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>

      {d ? (
        <dl className="grid grid-cols-2 gap-4 rounded-card border border-line bg-card p-4 md:grid-cols-4" data-testid="daybook-totals">
          <Stat label={t("totalIn")} value={money(d.totalIn)} testId="daybook-in" />
          <Stat label={t("totalOut")} value={money(d.totalOut)} testId="daybook-out" />
          <Stat label={t("net")} value={money(d.net)} testId="daybook-net" emphasis />
          <Stat label={t("thisPage")} value={money(pageNet)} testId="daybook-page-net" />
        </dl>
      ) : null}
      {d ? (
        <p className="text-sm text-muted" data-testid="daybook-range">
          {t("range", { from: dateOnly(d.from), to: dateOnly(d.to), count: total })}
        </p>
      ) : null}
      {state.filters.accountId ? <Alert variant="info">{t("accountNote")}</Alert> : null}
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => `${x.sourceType ?? "x"}-${x.sourceId ?? ""}-${x.at}-${x.amount}-${x.description}`} labels={labels} page={state.page} pageSize={state.pageSize} total={total} onPageChange={state.setPage} />
    </div>
  );
}
