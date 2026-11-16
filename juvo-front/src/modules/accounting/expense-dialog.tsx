"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { AttachmentPicker, type UploadedFile } from "@/components/data/attachment-picker";
import { usePermissions, useSession } from "@/components/providers/session-provider";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { parseAmount, round2 } from "@/modules/purchasing/logic";
import { uploadExpenseFile, useAccounts, useExpenseCategories, useSaveExpense, useSubmitExpense, type ExpenseDetail } from "./api";
import { rememberSubmitted, useMoney2 } from "./common";
import { MONEY_METHODS, expenseProblem, receiptProblem, todayDhaka } from "./logic";
import { useAccountingPermissions } from "./permissions";

type Props = { open: boolean; onOpenChange: (open: boolean) => void; expense: ExpenseDetail | null; onSaved?: (expense: ExpenseDetail) => void };

/** Create or change a draft expense. The body is mounted only while the dialog is open, so the fields start fresh every time. */
export function ExpenseDialog({ open, onOpenChange, expense, onSaved }: Props) {
  const t = useTranslations("accounting.expenseForm");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="md" closeLabel={t("close")}>
        <Body expense={expense} close={() => onOpenChange(false)} onSaved={onSaved} />
      </ModalContent>
    </Dialog>
  );
}

function Body({ expense, close, onSaved }: { expense: ExpenseDetail | null; close: () => void; onSaved?: (expense: ExpenseDetail) => void }) {
  const t = useTranslations("accounting.expenseForm");
  const tc = useTranslations("accounting.common");
  const tm = useTranslations("accounting.method");
  const report = useReportError();
  const money = useMoney2();
  const { me } = useSession();
  const { activeBranchId } = usePermissions();
  const perms = useAccountingPermissions();
  const branches = useBranchOptions();
  const save = useSaveExpense();
  const submit = useSubmitExpense();

  const [branchId, setBranchId] = React.useState(expense?.branchId ?? (activeBranchId && branches.some((b) => b.value === activeBranchId) ? activeBranchId : (branches[0]?.value ?? "")));
  const [categoryId, setCategoryId] = React.useState(expense?.categoryId ?? "");
  const [accountId, setAccountId] = React.useState(expense?.accountId ?? "");
  const [amount, setAmount] = React.useState(expense ? String(toNumber(expense.amount)) : "");
  const [date, setDate] = React.useState(expense?.expenseDate ?? todayDhaka());
  const [method, setMethod] = React.useState<string>(expense?.method ?? "Cash");
  const [reference, setReference] = React.useState(expense?.reference ?? "");
  const [notes, setNotes] = React.useState(expense?.notes ?? "");
  const [added, setAdded] = React.useState<UploadedFile[]>([]);
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  const categories = useExpenseCategories(false);
  const accounts = useAccounts({ branchId: branchId || undefined, enabled: perms.cashbookView && !!branchId });
  const categoryOptions = React.useMemo(() => {
    const all = categories.data ?? [];
    const byId = new Map(all.map((c) => [c.id, c]));
    return all.map((c) => ({ value: c.id, label: c.parentId && byId.get(c.parentId) ? `${byId.get(c.parentId)!.nameEn} / ${c.nameEn}` : c.nameEn }));
  }, [categories.data]);
  const accountOptions = React.useMemo(() => (accounts.data ?? []).filter((a) => a.isActive || a.id === expense?.accountId).map((a) => ({ value: a.id, label: `${a.name} · ${money(a.balance)}` })), [accounts.data, expense?.accountId, money]);

  const run = async (andSubmit: boolean) => {
    const problem = expenseProblem({ categoryId, accountId, amount, date, method, branchId });
    const next: Record<string, string> = {};
    if (problem) next[problem] = t(`errors.${problem}`);
    setErrors(next);
    if (problem) return;
    try {
      let saved = await save.mutateAsync({
        id: expense?.id,
        body: {
          branchId: expense ? null : branchId,
          categoryId,
          expenseDate: date,
          amount: round2(parseAmount(amount)!),
          method: method as (typeof MONEY_METHODS)[number],
          accountId,
          supplierId: expense?.supplierId ?? null,
          reference: reference.trim() || null,
          notes: notes.trim() || null,
          attachmentPaths: added.map((f) => f.path!).filter(Boolean),
        },
      });
      if (andSubmit) {
        saved = await submit.mutateAsync(saved.id);
        rememberSubmitted(me.staff?.id ?? null, saved.id);
      }
      toast.success(andSubmit ? t("sent") : expense ? t("saved") : t("created"));
      onSaved?.(saved);
      close();
    } catch (e) {
      report(e);
    }
  };

  const attachLabels = {
    add: t("attachAdd"),
    uploading: t("attachUploading"),
    remove: (name: string) => t("attachRemove", { name }),
    hint: t("attachHint"),
    typeError: t("attachType"),
    sizeError: t("attachSize"),
    open: (name: string) => t("attachOpen", { name }),
  };
  const busy = save.isPending || submit.isPending;

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        void run(false);
      }}
      noValidate
    >
      <DialogTitle className="font-display text-xl font-semibold text-strong">{expense ? t("editTitle", { number: expense.number }) : t("newTitle")}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{t("description")}</DialogDescription>
      <div className="mt-4 grid gap-4 sm:grid-cols-2">
        {!expense && branches.length > 1 ? (
          <Field label={t("branch")} htmlFor="ex-branch" error={errors.branch} required>
            <Select id="ex-branch" value={branchId} onValueChange={(v) => { setBranchId(v); setAccountId(""); }} options={branches} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        ) : null}
        <Field label={t("category")} htmlFor="ex-category" error={errors.category} required>
          <Select id="ex-category" value={categoryId} onValueChange={setCategoryId} options={categoryOptions} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.category} />
        </Field>
        <Field label={t("amount")} htmlFor="ex-amount" error={errors.amount} required>
          <Input id="ex-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-invalid={!!errors.amount} autoComplete="off" />
        </Field>
        <Field label={t("date")} htmlFor="ex-date" error={errors.date} required>
          <Input id="ex-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} aria-invalid={!!errors.date} />
        </Field>
        <Field label={t("method")} htmlFor="ex-method">
          <Select id="ex-method" value={method} onValueChange={setMethod} options={MONEY_METHODS.map((m) => ({ value: m, label: tm(m) }))} placeholder={t("method")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <Field label={t("account")} htmlFor="ex-account" error={errors.account} hint={t("accountHint")} required className="sm:col-span-2">
          {perms.cashbookView ? (
            <Select id="ex-account" value={accountId} onValueChange={setAccountId} options={accountOptions} placeholder={accounts.isLoading ? tc("loading") : t("choose")} searchPlaceholder={tc("search")} emptyText={t("noAccounts")} invalid={!!errors.account} />
          ) : (
            <Alert variant="warning">{t("needCashbook")}</Alert>
          )}
        </Field>
        <Field label={t("reference")} htmlFor="ex-ref" className="sm:col-span-2">
          <Input id="ex-ref" value={reference} maxLength={120} onChange={(e) => setReference(e.target.value)} autoComplete="off" />
        </Field>
        <Field label={t("notes")} htmlFor="ex-notes" className="sm:col-span-2">
          <Textarea id="ex-notes" rows={2} maxLength={1000} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </Field>
        <div className="sm:col-span-2">
          <p className="mb-1.5 text-sm font-semibold text-strong">{t("receipt")}</p>
          <AttachmentPicker idBase="ex" saved={expense?.attachments ?? []} added={added} onAdded={setAdded} upload={(f) => uploadExpenseFile(f)} check={receiptProblem} labels={attachLabels} onError={report} />
        </div>
      </div>
      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={busy}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" variant="secondary" loading={busy} data-testid="expense-save">
          {t("saveDraft")}
        </Button>
        {perms.expenseCreate ? (
          <Button type="button" loading={busy} onClick={() => void run(true)} data-testid="expense-save-send">
            {t("saveAndSend")}
          </Button>
        ) : null}
      </div>
    </form>
  );
}
