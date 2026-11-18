"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Paperclip, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { FilterSelect, SearchBox, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { useAccounts, useExpenseCategories, useExpenses, type ExpenseListItem } from "./api";
import { ExpenseStatusBadge, useDateOnly, useMoney2 } from "./common";
import { EXPENSE_STATUSES } from "./logic";
import { useAccountingPermissions } from "./permissions";

const ExpenseDialog = dynamic(() => import("./expense-dialog").then((m) => m.ExpenseDialog), { ssr: false });

type Filters = { status?: string; categoryId?: string; branchId?: string; accountId?: string; from?: string; to?: string };
const INITIAL_SORT = [{ id: "date", desc: true }];

/** The expenses tab: list with search, filters, totals of the filtered rows, server paging; cards on phones. */
export function ExpensesList() {
  const t = useTranslations("accounting.expenses");
  const tc = useTranslations("accounting.common");
  const ts = useTranslations("accounting.status");
  const labels = useTableLabels();
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const router = useRouter();
  const branches = useBranchOptions();
  const perms = useAccountingPermissions();
  const state = useListState<Filters>({}, undefined, INITIAL_SORT);
  const [creating, setCreating] = React.useState(false);

  const query = useExpenses({ page: state.page, pageSize: state.pageSize, search: state.search, sort: state.sort, ...state.filters });
  const items = React.useMemo(() => query.data?.expenses.items ?? [], [query.data?.expenses.items]);
  const total = toNumber(query.data?.expenses.total);

  const columns = React.useMemo<ColumnDef<ExpenseListItem, unknown>[]>(
    () => [
      {
        id: "number",
        accessorFn: (r) => r.number,
        header: t("columns.number"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/accounting/expenses/${row.original.id}`} className="inline-flex min-h-11 min-w-11 items-center whitespace-nowrap rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="font-mono">{row.original.number}</span>
          </Link>
        ),
      },
      { id: "date", accessorFn: (r) => r.expenseDate, header: t("columns.date"), meta: { secondary: true }, cell: ({ row }) => <span className="whitespace-nowrap text-muted">{dateOnly(row.original.expenseDate)}</span> },
      { id: "category", header: t("columns.category"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.categoryName}</span> },
      { id: "amount", accessorFn: (r) => r.amount, header: t("columns.amount"), meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="font-medium tabular-nums text-strong">{money(row.original.amount)}</span> },
      {
        id: "status",
        accessorFn: (r) => r.status,
        header: t("columns.status"),
        meta: { secondary: true },
        cell: ({ row }) => <ExpenseStatusBadge status={row.original.status} auto={row.original.autoApproved} />,
      },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "account", header: t("columns.account"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.accountName}</span> },
      {
        id: "files",
        header: t("columns.files"),
        enableSorting: false,
        meta: { hideOnMobile: true, numeric: true },
        cell: ({ row }) =>
          toNumber(row.original.attachmentCount) > 0 ? (
            <span className="inline-flex items-center gap-1 tabular-nums">
              <Paperclip className="size-4" aria-hidden /> {toNumber(row.original.attachmentCount)}
            </span>
          ) : (
            <span className="text-muted">-</span>
          ),
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/accounting/expenses/${row.original.id}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, money, dateOnly],
  );

  const filtersActive = !!state.searchText || Object.values(state.filters).some(Boolean);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <dl className="flex flex-wrap items-baseline gap-x-6 gap-y-1" data-testid="expense-totals">
          <div>
            <dt className="inline text-sm text-muted">{t("count")}: </dt>
            <dd className="inline font-semibold tabular-nums text-strong" aria-live="polite">{total}</dd>
          </div>
          <div>
            <dt className="inline text-sm text-muted">{t("totalAmount")}: </dt>
            <dd className="inline text-lg font-semibold tabular-nums text-strong" data-testid="expense-total-amount">{money(query.data?.totalAmount)}</dd>
          </div>
        </dl>
        {perms.expenseCreate ? (
          <Button onClick={() => setCreating(true)} data-testid="new-expense">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={EXPENSE_STATUSES.map((x) => ({ value: x, label: ts(x) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <CategoryFilter value={state.filters.categoryId} onChange={(v) => state.setFilter("categoryId", v)} />
        {branches.length > 1 ? (
          <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        {perms.cashbookView ? <AccountFilter value={state.filters.accountId} onChange={(v) => state.setFilter("accountId", v)} /> : null}
        <Field label={tc("from")} htmlFor="ex-from">
          <Input id="ex-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={tc("to")} htmlFor="ex-to">
          <Input id="ex-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
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
        total={total}
        onPageChange={state.setPage}
        sorting={state.sorting}
        onSortingChange={state.setSorting}
      />
      {creating ? <ExpenseDialog open={creating} onOpenChange={setCreating} expense={null} onSaved={(saved) => router.push(`/admin/accounting/expenses/${saved.id}`)} /> : null}
    </div>
  );
}

function CategoryFilter({ value, onChange }: { value: string | undefined; onChange: (v: string | undefined) => void }) {
  const t = useTranslations("accounting.expenses");
  const tc = useTranslations("accounting.common");
  const categories = useExpenseCategories(true);
  const options = React.useMemo(() => (categories.data ?? []).map((c) => ({ value: c.id, label: c.nameEn })), [categories.data]);
  return <FilterSelect value={value} onChange={onChange} options={options} allLabel={t("anyCategory")} label={t("columns.category")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />;
}

function AccountFilter({ value, onChange }: { value: string | undefined; onChange: (v: string | undefined) => void }) {
  const t = useTranslations("accounting.expenses");
  const tc = useTranslations("accounting.common");
  const accounts = useAccounts({ includeInactive: true });
  const options = React.useMemo(() => (accounts.data ?? []).map((a) => ({ value: a.id, label: `${a.branchCode} · ${a.name}` })), [accounts.data]);
  return <FilterSelect value={value} onChange={onChange} options={options} allLabel={t("anyAccount")} label={t("columns.account")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />;
}
