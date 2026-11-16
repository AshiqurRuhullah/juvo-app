"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { AlertTriangle, ArrowLeftRight, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import { FilterSelect, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { useAccounts, useBalances, type Account, type AccountBalances } from "./api";
import { Stat, useMoney2 } from "@/modules/purchasing/common";
import { balanceState } from "./logic";
import { useAccountingPermissions } from "./permissions";

const AccountDialog = dynamic(() => import("./account-dialogs").then((m) => m.AccountDialog), { ssr: false });
const TransferDialog = dynamic(() => import("./account-dialogs").then((m) => m.TransferDialog), { ssr: false });
const AdjustDialog = dynamic(() => import("./account-dialogs").then((m) => m.AdjustDialog), { ssr: false });
const EntriesDialog = dynamic(() => import("./account-dialogs").then((m) => m.EntriesDialog), { ssr: false });

type Row = AccountBalances["accounts"][number];
type Dialog = { kind: "edit" | "new" | "transfer" | "adjust" | "entries"; accountId?: string } | null;

/** The accounts tab: live balances as of a date (opening + money in - money out), and the actions on each account. */
export function AccountsView() {
  const t = useTranslations("accounting.accounts");
  const tt = useTranslations("accounting.accountType");
  const tc = useTranslations("accounting.common");
  const labels = useTableLabels();
  const money = useMoney2();
  const perms = useAccountingPermissions();
  const branches = useBranchOptions();
  const [asOf, setAsOf] = React.useState("");
  const [branchId, setBranchId] = React.useState<string | undefined>();
  const [dialog, setDialog] = React.useState<Dialog>(null);

  // Both calls start together: balances for the numbers, accounts for the details an edit needs (number, note).
  const balances = useBalances(asOf || undefined, branchId);
  const accounts = useAccounts({ includeInactive: true, branchId });
  const rows = React.useMemo(() => balances.data?.accounts ?? [], [balances.data?.accounts]);
  const byId = React.useMemo(() => new Map((accounts.data ?? []).map((a) => [a.id, a])), [accounts.data]);
  const target = dialog?.accountId ? byId.get(dialog.accountId) : undefined;
  const close = () => setDialog(null);
  const open = React.useCallback((kind: NonNullable<Dialog>["kind"], accountId?: string) => setDialog({ kind, accountId }), []);

  const columns = React.useMemo<ColumnDef<Row, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block [overflow-wrap:anywhere]">
            <span className="font-semibold text-strong">{row.original.name}</span>
            <span className="block text-sm text-muted">
              {tt(row.original.type)} · <span className="font-mono">{row.original.branchCode}</span>
            </span>
          </span>
        ),
      },
      { id: "opening", header: t("columns.opening"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.opening)}</span> },
      { id: "in", header: t("columns.in"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.moneyIn)}</span> },
      { id: "out", header: t("columns.out"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.moneyOut)}</span> },
      {
        id: "balance",
        header: t("columns.balance"),
        enableSorting: false,
        meta: { numeric: true, secondary: true },
        cell: ({ row }) => {
          const state = balanceState(toNumber(row.original.balance));
          return (
            <span className="inline-flex flex-col items-end">
              <span className="font-semibold tabular-nums text-strong" data-testid="account-balance" data-name={row.original.name}>
                {money(row.original.balance)}
              </span>
              {state === "overdrawn" ? (
                <span className="inline-flex items-center gap-1 text-xs font-medium text-danger-ink">
                  <AlertTriangle className="size-3.5" aria-hidden /> {t("overdrawn")}
                </span>
              ) : null}
            </span>
          );
        },
      },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant={row.original.isActive ? "success" : "outline"}>{row.original.isActive ? t("active") : t("inactive")}</Badge> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <span className="flex flex-wrap justify-end gap-2">
            <Button variant="secondary" onClick={() => open("entries", row.original.id)} aria-label={t("entriesFor", { name: row.original.name })}>
              {t("entries")}
            </Button>
            {perms.cashbookEdit ? (
              <>
                <Button variant="secondary" onClick={() => open("adjust", row.original.id)} aria-label={t("adjustFor", { name: row.original.name })}>
                  {t("adjust")}
                </Button>
                <Button variant="secondary" onClick={() => open("edit", row.original.id)} aria-label={t("editFor", { name: row.original.name })}>
                  {t("edit")}
                </Button>
              </>
            ) : null}
          </span>
        ),
      },
    ],
    [t, tt, money, perms.cashbookEdit, open],
  );

  const d = balances.data;
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t("asOf")} htmlFor="ac-asof" hint={t("asOfHint")}>
            <Input id="ac-asof" type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} />
          </Field>
          {branches.length > 1 ? (
            <div className="self-end">
              <FilterSelect value={branchId} onChange={setBranchId} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
            </div>
          ) : null}
        </div>
        {perms.cashbookEdit ? (
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => open("transfer")} data-testid="new-transfer">
              <ArrowLeftRight aria-hidden /> {t("transfer")}
            </Button>
            <Button onClick={() => open("new")} data-testid="new-account">
              <Plus aria-hidden /> {t("new")}
            </Button>
          </div>
        ) : null}
      </div>

      {d ? (
        <dl className="grid grid-cols-2 gap-4 rounded-card border border-line bg-card p-4" data-testid="balances-summary">
          <Stat label={t("totalBalance", { date: d.asOf })} value={money(d.total)} testId="balances-total" emphasis />
          <Stat label={t("derived")} value={money(d.derivedReceipts)} testId="balances-derived" />
        </dl>
      ) : null}
      {d ? <p className="text-sm text-muted">{t("derivedNote")}</p> : null}

      {balances.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={rows} loading={balances.isLoading} getRowId={(x) => x.id} labels={labels} />

      {dialog?.kind === "new" ? <AccountDialog account={null} onClose={close} /> : null}
      {dialog?.kind === "edit" && target ? <AccountDialog account={target} onClose={close} /> : null}
      {dialog?.kind === "transfer" ? <TransferDialog accounts={accounts.data ?? []} onClose={close} /> : null}
      {dialog?.kind === "adjust" && target ? <AdjustDialog account={target as Account} onClose={close} /> : null}
      {dialog?.kind === "entries" && target ? <EntriesDialog account={target as Account} onClose={close} /> : null}
    </div>
  );
}
