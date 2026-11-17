"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, ChoiceRow, Field, Input, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useTableLabels } from "@/components/data/list-kit";
import { usePermissions } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { parseAmount, round2 } from "@/modules/purchasing/logic";
import { useAccountEntries, useAdjust, useSaveAccount, useTransfer, type Account, type LedgerEntry } from "./api";
import { Flow, useDateOnly, useMoney2 } from "./common";
import { ACCOUNT_TYPES, adjustmentProblem, parseSigned, todayDhaka, transferProblem } from "./logic";
import { PAGE_SIZE } from "@/lib/pagination";

function Shell({ title, description, onClose, children, size = "md" }: { title: string; description: string; onClose: () => void; children: React.ReactNode; size?: "sm" | "md" | "lg" }) {
  const tc = useTranslations("accounting.common");
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <ModalContent size={size} closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{title}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{description}</DialogDescription>
        {children}
      </ModalContent>
    </Dialog>
  );
}

function Footer({ busy, confirm, testId }: { busy: boolean; confirm: string; testId: string }) {
  const tc = useTranslations("accounting.common");
  return (
    <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
      <DialogClose asChild>
        <Button type="button" variant="secondary" disabled={busy}>
          {tc("cancel")}
        </Button>
      </DialogClose>
      <Button type="submit" loading={busy} data-testid={testId}>
        {confirm}
      </Button>
    </div>
  );
}

function useFailure() {
  const message = useProblemMessage();
  const [failure, setFailure] = React.useState<string | null>(null);
  return { failure, setFailure, fail: (e: unknown) => setFailure(message(e instanceof ApiError ? e.problem : null)) };
}

/* ---- Account: create or edit -------------------------------------------------------------------------------------------- */

export function AccountDialog({ account, onClose }: { account: Account | null; onClose: () => void }) {
  const t = useTranslations("accounting.accountForm");
  const tc = useTranslations("accounting.common");
  const tt = useTranslations("accounting.accountType");
  const report = useReportError();
  const branches = useBranchOptions();
  const { activeBranchId } = usePermissions();
  const save = useSaveAccount();
  const [name, setName] = React.useState(account?.name ?? "");
  const [type, setType] = React.useState<string>(account?.type ?? "Cash");
  const [number, setNumber] = React.useState(account?.accountNumber ?? "");
  const [note, setNote] = React.useState(account?.note ?? "");
  const [branchId, setBranchId] = React.useState(account?.branchId ?? (activeBranchId && branches.some((b) => b.value === activeBranchId) ? activeBranchId : (branches[0]?.value ?? "")));
  const [opening, setOpening] = React.useState("0");
  const [active, setActive] = React.useState(account?.isActive ?? true);
  const [errors, setErrors] = React.useState<{ name?: string; opening?: string; branch?: string }>({});

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: typeof errors = {};
    if (!name.trim()) next.name = t("nameRequired");
    if (!account && !branchId) next.branch = t("branchRequired");
    const open = parseSigned(opening || "0");
    if (!account && open === null) next.opening = t("openingInvalid");
    setErrors(next);
    if (Object.keys(next).length) return;
    const text = (s: string) => s.trim() || null;
    try {
      if (account) {
        await save.mutateAsync({ id: account.id, update: { name: name.trim(), type: type as (typeof ACCOUNT_TYPES)[number], accountNumber: text(number), note: text(note), isActive: active } });
      } else {
        await save.mutateAsync({ create: { branchId, name: name.trim(), type: type as (typeof ACCOUNT_TYPES)[number], accountNumber: text(number), note: text(note), openingBalance: round2(open!), openingDate: null } });
      }
      toast.success(account ? t("saved") : t("created"));
      onClose();
    } catch (e) {
      report(e);
    }
  };

  return (
    <Shell title={account ? t("editTitle") : t("newTitle")} description={t("description")} onClose={onClose}>
      <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 grid gap-4 sm:grid-cols-2">
        <Field label={t("name")} htmlFor="ac-name" required error={errors.name} className="sm:col-span-2">
          <Input id="ac-name" value={name} maxLength={120} onChange={(e) => setName(e.target.value)} aria-invalid={!!errors.name} autoComplete="off" />
        </Field>
        <Field label={t("type")} htmlFor="ac-type">
          <Select id="ac-type" value={type} onValueChange={setType} options={ACCOUNT_TYPES.map((x) => ({ value: x, label: tt(x) }))} placeholder={t("type")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <Field label={t("number")} htmlFor="ac-number" hint={t("numberHint")}>
          <Input id="ac-number" value={number} maxLength={60} onChange={(e) => setNumber(e.target.value)} autoComplete="off" />
        </Field>
        {!account ? (
          <>
            <Field label={t("branch")} htmlFor="ac-branch" required error={errors.branch}>
              <Select id="ac-branch" value={branchId} onValueChange={setBranchId} options={branches} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.branch} />
            </Field>
            <Field label={t("opening")} htmlFor="ac-opening" hint={t("openingHint")} error={errors.opening}>
              <Input id="ac-opening" inputMode="decimal" value={opening} onChange={(e) => setOpening(e.target.value)} aria-invalid={!!errors.opening} autoComplete="off" />
            </Field>
          </>
        ) : null}
        <Field label={t("note")} htmlFor="ac-note" className="sm:col-span-2">
          <Textarea id="ac-note" rows={2} maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        {account ? (
          <>
            <ChoiceRow htmlFor="ac-active" className="sm:col-span-2">
              <Checkbox id="ac-active" checked={active} onCheckedChange={(c) => setActive(c === true)} />
              {t("active")}
            </ChoiceRow>
            <Alert variant="info" className="sm:col-span-2">
              {t("openingFixed")}
            </Alert>
          </>
        ) : null}
        <div className="sm:col-span-2">
          <Footer busy={save.isPending} confirm={tc("save")} testId="account-save" />
        </div>
      </form>
    </Shell>
  );
}

/* ---- Transfer ------------------------------------------------------------------------------------------------------------- */

export function TransferDialog({ accounts, onClose, fromId }: { accounts: Account[]; onClose: () => void; fromId?: string }) {
  const t = useTranslations("accounting.transfer");
  const tc = useTranslations("accounting.common");
  const money = useMoney2();
  const transfer = useTransfer();
  const { failure, setFailure, fail } = useFailure();
  const [from, setFrom] = React.useState(fromId ?? "");
  const [to, setTo] = React.useState("");
  const [amount, setAmount] = React.useState("");
  const [date, setDate] = React.useState(todayDhaka());
  const [note, setNote] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const options = React.useMemo(() => accounts.filter((a) => a.isActive).map((a) => ({ value: a.id, label: `${a.branchCode} · ${a.name} · ${money(a.balance)}` })), [accounts, money]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const problem = transferProblem(from, to, amount);
    setError(problem ? t(`errors.${problem}`) : null);
    if (problem) return;
    try {
      await transfer.mutateAsync({ fromAccountId: from, toAccountId: to, amount: round2(parseAmount(amount)!), date: date || null, note: note.trim() || null });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      fail(e);
    }
  };

  return (
    <Shell title={t("title")} description={t("description")} onClose={onClose}>
      <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-4">
        <Field label={t("from")} htmlFor="tf-from" required>
          <Select id="tf-from" value={from} onValueChange={setFrom} options={options} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <Field label={t("to")} htmlFor="tf-to" required>
          <Select id="tf-to" value={to} onValueChange={setTo} options={options.filter((o) => o.value !== from)} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("amount")} htmlFor="tf-amount" required error={error ?? undefined}>
            <Input id="tf-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-invalid={!!error} autoComplete="off" />
          </Field>
          <Field label={t("date")} htmlFor="tf-date">
            <Input id="tf-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
          </Field>
        </div>
        <Field label={t("note")} htmlFor="tf-note">
          <Textarea id="tf-note" rows={2} maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        {failure ? (
          <Alert variant="danger" data-testid="dialog-error">
            {failure}
          </Alert>
        ) : null}
        <Footer busy={transfer.isPending} confirm={t("confirm")} testId="transfer-confirm" />
      </form>
    </Shell>
  );
}

/* ---- Adjustment ----------------------------------------------------------------------------------------------------------- */

export function AdjustDialog({ account, onClose }: { account: Account; onClose: () => void }) {
  const t = useTranslations("accounting.adjust");
  const money = useMoney2();
  const adjust = useAdjust();
  const { failure, setFailure, fail } = useFailure();
  const [amount, setAmount] = React.useState("");
  const [date, setDate] = React.useState(todayDhaka());
  const [reason, setReason] = React.useState("");
  const [errors, setErrors] = React.useState<{ amount?: string; reason?: string }>({});

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const problem = adjustmentProblem(amount, reason);
    setErrors(problem === "amount" ? { amount: t("errors.amount") } : problem === "reason" ? { reason: t("errors.reason") } : {});
    if (problem) return;
    try {
      await adjust.mutateAsync({ accountId: account.id, amount: round2(parseSigned(amount)!), date: date || null, reason: reason.trim() });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      fail(e);
    }
  };

  return (
    <Shell title={t("title", { name: account.name })} description={t("description", { balance: money(account.balance) })} onClose={onClose}>
      <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-4">
        <Field label={t("amount")} htmlFor="adj-amount" hint={t("amountHint")} required error={errors.amount}>
          <Input id="adj-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-invalid={!!errors.amount} autoComplete="off" />
        </Field>
        <Field label={t("date")} htmlFor="adj-date">
          <Input id="adj-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </Field>
        <Field label={t("reason")} htmlFor="adj-reason" required error={errors.reason}>
          <Textarea id="adj-reason" rows={3} maxLength={300} value={reason} onChange={(e) => setReason(e.target.value)} aria-invalid={!!errors.reason} />
        </Field>
        {failure ? (
          <Alert variant="danger" data-testid="dialog-error">
            {failure}
          </Alert>
        ) : null}
        <Footer busy={adjust.isPending} confirm={t("confirm")} testId="adjust-confirm" />
      </form>
    </Shell>
  );
}

/* ---- Entries of one account ----------------------------------------------------------------------------------------------- */

export function EntriesDialog({ account, onClose }: { account: Account; onClose: () => void }) {
  const t = useTranslations("accounting.entries");
  const tk = useTranslations("accounting.kind");
  const labels = useTableLabels();
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const [page, setPage] = React.useState(1);
  const query = useAccountEntries(account.id, page, 10);
  const items = React.useMemo(() => query.data?.items ?? [], [query.data?.items]);

  const columns = React.useMemo<ColumnDef<LedgerEntry, unknown>[]>(
    () => [
      { id: "date", header: t("columns.date"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="whitespace-nowrap">{dateOnly(row.original.entryDate)}</span> },
      { id: "kind", header: t("columns.kind"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span>{tk.has(row.original.kind) ? tk(row.original.kind) : row.original.kind}</span> },
      { id: "description", header: t("columns.description"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.description}</span> },
      { id: "amount", header: t("columns.amount"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <Flow amount={toNumber(row.original.amount)} format={money} /> },
    ],
    [t, tk, dateOnly, money],
  );

  return (
    <Shell title={t("title", { name: account.name })} description={t("description")} onClose={onClose} size="lg">
      <div className="mt-4" data-testid="entries">
        <DataTable caption={t("title", { name: account.name })} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} page={page} pageSize={PAGE_SIZE} total={toNumber(query.data?.total)} onPageChange={setPage} />
      </div>
    </Shell>
  );
}
