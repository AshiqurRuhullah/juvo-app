"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Play, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, ChoiceRow, Field, Input, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useTableLabels } from "@/components/data/list-kit";
import { usePermissions } from "@/components/providers/session-provider";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { parseAmount, parseQuantity, round2 } from "@/modules/purchasing/logic";
import { useAccounts, useExpenseCategories, useRecurring, useRunRecurring, useSaveRecurring, type Recurring } from "./api";
import { useDateOnly, useMoney2 } from "./common";
import { MONEY_METHODS, dayOfMonthProblem, todayDhaka } from "./logic";
import { useAccountingPermissions } from "./permissions";

/** The repeating tab: monthly templates; "run now" creates this month's expenses that are due and reports how many. */
export function RecurringView() {
  const t = useTranslations("accounting.recurring");
  const tc = useTranslations("accounting.common");
  const labels = useTableLabels();
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const report = useReportError();
  const perms = useAccountingPermissions();
  const query = useRecurring();
  const run = useRunRecurring();
  const [editing, setEditing] = React.useState<Recurring | null>(null);
  const [open, setOpen] = React.useState(false);
  const [result, setResult] = React.useState<{ templates: number; created: number; autoApproved: number } | null>(null);
  const items = React.useMemo(() => query.data ?? [], [query.data]);

  const edit = React.useCallback((r: Recurring | null) => {
    setEditing(r);
    setOpen(true);
  }, []);

  const runNow = async () => {
    try {
      const r = await run.mutateAsync();
      setResult({ templates: toNumber(r.templates), created: toNumber(r.created), autoApproved: toNumber(r.autoApproved) });
    } catch (e) {
      report(e);
    }
  };

  const columns = React.useMemo<ColumnDef<Recurring, unknown>[]>(
    () => [
      { id: "title", header: t("columns.title"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="font-semibold text-strong [overflow-wrap:anywhere]">{row.original.title}</span> },
      { id: "category", header: t("columns.category"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span>{row.original.categoryName}</span> },
      { id: "amount", header: t("columns.amount"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="font-medium tabular-nums text-strong">{money(row.original.amount)}</span> },
      { id: "day", header: t("columns.day"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span>{t("dayOf", { day: toNumber(row.original.dayOfMonth) })}</span> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "account", header: t("columns.account"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span>{row.original.accountName}</span> },
      { id: "period", header: t("columns.period"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="whitespace-nowrap text-muted">{dateOnly(row.original.startDate)}{row.original.endDate ? ` - ${dateOnly(row.original.endDate)}` : ""}</span> },
      { id: "last", header: t("columns.last"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span>{row.original.lastGeneratedPeriod ?? "-"}</span> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant={row.original.isActive ? "success" : "outline"}>{row.original.isActive ? t("active") : t("inactive")}</Badge> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) =>
          perms.expenseEdit ? (
            <Button variant="secondary" onClick={() => edit(row.original)} aria-label={t("editFor", { title: row.original.title })}>
              {t("edit")}
            </Button>
          ) : null,
      },
    ],
    [t, money, dateOnly, perms.expenseEdit, edit],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted">{t("hint")}</p>
        {perms.expenseEdit ? (
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => void runNow()} loading={run.isPending} data-testid="run-now">
              <Play aria-hidden /> {t("runNow")}
            </Button>
            <Button onClick={() => edit(null)} data-testid="new-recurring">
              <Plus aria-hidden /> {t("new")}
            </Button>
          </div>
        ) : null}
      </div>
      {result ? (
        <Alert variant="success" data-testid="run-result">
          {t("result", { templates: result.templates, created: result.created, auto: result.autoApproved })}
        </Alert>
      ) : null}
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} />
      {open ? <RecurringDialog open={open} onOpenChange={setOpen} item={editing} /> : null}
    </div>
  );
}

function RecurringDialog({ open, onOpenChange, item }: { open: boolean; onOpenChange: (open: boolean) => void; item: Recurring | null }) {
  const t = useTranslations("accounting.recurring.form");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="md" closeLabel={t("close")}>
        <Body item={item} close={() => onOpenChange(false)} />
      </ModalContent>
    </Dialog>
  );
}

function Body({ item, close }: { item: Recurring | null; close: () => void }) {
  const t = useTranslations("accounting.recurring.form");
  const tc = useTranslations("accounting.common");
  const tm = useTranslations("accounting.method");
  const report = useReportError();
  const money = useMoney2();
  const perms = useAccountingPermissions();
  const branches = useBranchOptions();
  const { activeBranchId } = usePermissions();
  const save = useSaveRecurring();
  const [branchId, setBranchId] = React.useState(item?.branchId ?? (activeBranchId && branches.some((b) => b.value === activeBranchId) ? activeBranchId : (branches[0]?.value ?? "")));
  const [title, setTitle] = React.useState(item?.title ?? "");
  const [categoryId, setCategoryId] = React.useState(item?.categoryId ?? "");
  const [accountId, setAccountId] = React.useState(item?.accountId ?? "");
  const [amount, setAmount] = React.useState(item ? String(toNumber(item.amount)) : "");
  const [method, setMethod] = React.useState<string>(item?.method ?? "Cash");
  const [day, setDay] = React.useState(item ? String(toNumber(item.dayOfMonth)) : "1");
  const [start, setStart] = React.useState(item?.startDate ?? todayDhaka());
  const [end, setEnd] = React.useState(item?.endDate ?? "");
  const [notes, setNotes] = React.useState(item?.notes ?? "");
  const [active, setActive] = React.useState(item?.isActive ?? true);
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  const categories = useExpenseCategories(false);
  const accounts = useAccounts({ branchId: branchId || undefined, enabled: perms.cashbookView && !!branchId });
  const categoryOptions = React.useMemo(() => (categories.data ?? []).map((c) => ({ value: c.id, label: c.nameEn })), [categories.data]);
  const accountOptions = React.useMemo(() => (accounts.data ?? []).filter((a) => a.isActive || a.id === item?.accountId).map((a) => ({ value: a.id, label: `${a.name} · ${money(a.balance)}` })), [accounts.data, item?.accountId, money]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    if (!title.trim()) next.title = t("errors.title");
    if (!categoryId) next.category = t("errors.category");
    if (!accountId) next.account = t("errors.account");
    const n = parseAmount(amount);
    if (n === null || n <= 0) next.amount = t("errors.amount");
    if (dayOfMonthProblem(day)) next.day = t("errors.day");
    if (!start) next.start = t("errors.start");
    if (end && start && end < start) next.end = t("errors.end");
    setErrors(next);
    if (Object.keys(next).length) return;
    try {
      await save.mutateAsync({
        id: item?.id,
        body: {
          branchId: item ? null : branchId,
          title: title.trim(),
          categoryId,
          amount: round2(n!),
          method: method as (typeof MONEY_METHODS)[number],
          accountId,
          supplierId: item?.supplierId ?? null,
          notes: notes.trim() || null,
          dayOfMonth: parseQuantity(day)!,
          startDate: start,
          endDate: end || null,
          isActive: active,
        },
      });
      toast.success(item ? t("saved") : t("created"));
      close();
    } catch (e) {
      report(e);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{item ? t("editTitle") : t("newTitle")}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{t("description")}</DialogDescription>
      <div className="mt-4 grid gap-4 sm:grid-cols-2">
        <Field label={t("name")} htmlFor="rc-title" required error={errors.title} className="sm:col-span-2">
          <Input id="rc-title" value={title} maxLength={160} onChange={(e) => setTitle(e.target.value)} aria-invalid={!!errors.title} autoComplete="off" />
        </Field>
        {!item && branches.length > 1 ? (
          <Field label={t("branch")} htmlFor="rc-branch" required className="sm:col-span-2">
            <Select id="rc-branch" value={branchId} onValueChange={(v) => { setBranchId(v); setAccountId(""); }} options={branches} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        ) : null}
        <Field label={t("category")} htmlFor="rc-category" required error={errors.category}>
          <Select id="rc-category" value={categoryId} onValueChange={setCategoryId} options={categoryOptions} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.category} />
        </Field>
        <Field label={t("amount")} htmlFor="rc-amount" required error={errors.amount}>
          <Input id="rc-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-invalid={!!errors.amount} autoComplete="off" />
        </Field>
        <Field label={t("method")} htmlFor="rc-method">
          <Select id="rc-method" value={method} onValueChange={setMethod} options={MONEY_METHODS.map((m) => ({ value: m, label: tm(m) }))} placeholder={t("method")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <Field label={t("account")} htmlFor="rc-account" required error={errors.account}>
          {perms.cashbookView ? (
            <Select id="rc-account" value={accountId} onValueChange={setAccountId} options={accountOptions} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={t("noAccounts")} invalid={!!errors.account} />
          ) : (
            <Alert variant="warning">{t("needCashbook")}</Alert>
          )}
        </Field>
        <Field label={t("day")} htmlFor="rc-day" hint={t("dayHint")} error={errors.day}>
          <Input id="rc-day" inputMode="numeric" value={day} onChange={(e) => setDay(e.target.value)} aria-invalid={!!errors.day} autoComplete="off" />
        </Field>
        <Field label={t("start")} htmlFor="rc-start" required error={errors.start}>
          <Input id="rc-start" type="date" value={start} onChange={(e) => setStart(e.target.value)} aria-invalid={!!errors.start} />
        </Field>
        <Field label={t("end")} htmlFor="rc-end" hint={t("endHint")} error={errors.end}>
          <Input id="rc-end" type="date" value={end} min={start || undefined} onChange={(e) => setEnd(e.target.value)} aria-invalid={!!errors.end} />
        </Field>
        <Field label={t("notes")} htmlFor="rc-notes" className="sm:col-span-2">
          <Textarea id="rc-notes" rows={2} maxLength={1000} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </Field>
        {item ? (
          <ChoiceRow htmlFor="rc-active" className="sm:col-span-2">
            <Checkbox id="rc-active" checked={active} onCheckedChange={(c) => setActive(c === true)} />
            {t("active")}
          </ChoiceRow>
        ) : null}
      </div>
      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="recurring-save">
          {tc("save")}
        </Button>
      </div>
    </form>
  );
}
