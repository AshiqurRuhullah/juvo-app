"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { ApiError } from "@/lib/api/client";
import { toNumber } from "@/modules/catalog/format";
import { VariantText } from "@/modules/inventory/common";
import { useAccounts } from "@/modules/accounting/api";
import { todayDhaka } from "@/modules/accounting/logic";
import { useOrderAction, useReceiveGoods, type PurchaseDetail } from "./api";
import { useMoney2 } from "./common";
import { MONEY_METHODS, parseAmount, paymentProblem, receiveLines, receiveProblems, round2, type ReceiveRow } from "./logic";
import { usePurchasingPermissions } from "./permissions";

type Shared = { order: PurchaseDetail; onClose: () => void };

function Shell({ title, description, onClose, children, size = "md" }: { title: string; description: string; onClose: () => void; children: React.ReactNode; size?: "sm" | "md" | "lg" }) {
  const tc = useTranslations("purchasing.common");
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

function Footer({ busy, confirm, testId, disabled }: { busy: boolean; confirm: string; testId: string; disabled?: boolean }) {
  const tc = useTranslations("purchasing.common");
  return (
    <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
      <DialogClose asChild>
        <Button type="button" variant="secondary" disabled={busy}>
          {tc("cancel")}
        </Button>
      </DialogClose>
      <Button type="submit" loading={busy} disabled={disabled} data-testid={testId}>
        {confirm}
      </Button>
    </div>
  );
}

/** The text of an API refusal for the form's own alert (so a refused receipt or payment stays next to its fields). */
function useFailure() {
  const message = useProblemMessage();
  const [failure, setFailure] = React.useState<string | null>(null);
  return { failure, setFailure, fail: (e: unknown) => setFailure(message(e instanceof ApiError ? e.problem : null)) };
}

/* ---- Receive goods ------------------------------------------------------------------------------------------------------ */

export function ReceiveDialog({ order, onClose }: Shared) {
  const t = useTranslations("purchasing.dialogs.receive");
  const receive = useReceiveGoods(order.id);
  const { failure, setFailure, fail } = useFailure();
  const lines = React.useMemo(() => order.lines.filter((l) => toNumber(l.outstandingQuantity) > 0), [order.lines]);
  // The usual delivery is the full rest of the order: start there and let the person lower it.
  const [rows, setRows] = React.useState<Record<string, { accepted: string; rejected: string }>>(() => Object.fromEntries(lines.map((l) => [l.id, { accepted: String(toNumber(l.outstandingQuantity)), rejected: "" }])));
  const [note, setNote] = React.useState("");
  const [shown, setShown] = React.useState(false);

  const model: ReceiveRow[] = lines.map((l) => ({ lineId: l.id, outstanding: toNumber(l.outstandingQuantity), accepted: rows[l.id]?.accepted ?? "", rejected: rows[l.id]?.rejected ?? "" }));
  const problems = receiveProblems(model);
  const problemOf = (lineId: string) => problems.find((p) => p.lineId === lineId)?.kind;
  const set = (lineId: string, key: "accepted" | "rejected", value: string) => setRows((old) => ({ ...old, [lineId]: { ...old[lineId]!, [key]: value } }));

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setShown(true);
    setFailure(null);
    if (problems.length > 0) return;
    try {
      await receive.mutateAsync({ note: note.trim() || null, lines: receiveLines(model) });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      fail(e);
    }
  };

  return (
    <Shell title={t("title")} description={t("description", { number: order.number })} onClose={onClose} size="lg">
      <form onSubmit={(e) => void submit(e)} noValidate>
        <ul className="mt-4 divide-y divide-line rounded-control border border-line" data-testid="receive-lines">
          {lines.map((l) => {
            const kind = shown ? problemOf(l.id) : undefined;
            return (
              <li key={l.id} className="flex flex-col gap-3 p-3 lg:flex-row lg:items-start lg:justify-between" data-sku={l.sku}>
                <div className="min-w-0 lg:flex-1">
                  <p className="font-semibold text-strong [overflow-wrap:anywhere]">{l.productName}</p>
                  <VariantText sku={l.sku} colorName={l.colorName} size={l.size} />
                  <p className="text-sm text-muted">{t("expected", { count: toNumber(l.outstandingQuantity) })}</p>
                </div>
                <div className="flex flex-wrap gap-3">
                  <div className="w-32">
                    <label htmlFor={`rc-a-${l.id}`} className="mb-1 block text-sm font-semibold text-strong">
                      {t("accepted")}
                    </label>
                    <Input id={`rc-a-${l.id}`} inputMode="numeric" autoComplete="off" value={rows[l.id]?.accepted ?? ""} aria-invalid={!!kind} onChange={(e) => set(l.id, "accepted", e.target.value)} />
                  </div>
                  <div className="w-32">
                    <label htmlFor={`rc-r-${l.id}`} className="mb-1 block text-sm font-semibold text-strong">
                      {t("rejected")}
                    </label>
                    <Input id={`rc-r-${l.id}`} inputMode="numeric" autoComplete="off" value={rows[l.id]?.rejected ?? ""} onChange={(e) => set(l.id, "rejected", e.target.value)} />
                  </div>
                </div>
                {kind ? (
                  <p role="alert" className="text-sm font-medium text-danger lg:basis-full">
                    {kind === "over" ? t("over", { count: toNumber(l.outstandingQuantity) }) : t("invalid")}
                  </p>
                ) : null}
              </li>
            );
          })}
        </ul>
        {shown && problems.some((p) => p.kind === "nothing") ? (
          <p role="alert" className="mt-2 text-sm font-medium text-danger">
            {t("nothing")}
          </p>
        ) : null}
        <Field label={t("note")} htmlFor="rc-note" className="mt-4">
          <Textarea id="rc-note" rows={2} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        <Alert variant="info" className="mt-4">
          {t("costNote")}
        </Alert>
        {failure ? (
          <Alert variant="danger" className="mt-4" data-testid="dialog-error">
            {failure}
          </Alert>
        ) : null}
        <Footer busy={receive.isPending} confirm={t("confirm")} testId="receive-confirm" />
      </form>
    </Shell>
  );
}

/* ---- Supplier invoice ---------------------------------------------------------------------------------------------------- */

export function InvoiceDialog({ order, onClose }: Shared) {
  const t = useTranslations("purchasing.dialogs.invoice");
  const act = useOrderAction(order.id);
  const { failure, setFailure, fail } = useFailure();
  const [number, setNumber] = React.useState(order.supplierInvoiceNumber ?? "");
  const [date, setDate] = React.useState(order.supplierInvoiceDate ?? todayDhaka());
  const [due, setDue] = React.useState(order.dueDate ?? "");
  const [amount, setAmount] = React.useState(order.invoiceAmount === null || order.invoiceAmount === undefined ? "" : String(toNumber(order.invoiceAmount)));
  const [errors, setErrors] = React.useState<{ number?: string; date?: string; amount?: string }>({});

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const next: typeof errors = {};
    if (!number.trim()) next.number = t("numberRequired");
    if (!date) next.date = t("dateRequired");
    const parsed = amount.trim() ? parseAmount(amount) : null;
    if (amount.trim() && parsed === null) next.amount = t("amountInvalid");
    setErrors(next);
    if (Object.keys(next).length) return;
    try {
      await act.mutateAsync({ kind: "invoice", number: number.trim(), invoiceDate: date, dueDate: due || null, amount: parsed });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      fail(e);
    }
  };

  return (
    <Shell title={t("title")} description={t("description", { number: order.number })} onClose={onClose}>
      <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-4">
        <Field label={t("number")} htmlFor="iv-number" required error={errors.number}>
          <Input id="iv-number" value={number} maxLength={80} onChange={(e) => setNumber(e.target.value)} aria-invalid={!!errors.number} autoComplete="off" />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("date")} htmlFor="iv-date" required error={errors.date}>
            <Input id="iv-date" type="date" value={date} onChange={(e) => setDate(e.target.value)} aria-invalid={!!errors.date} />
          </Field>
          <Field label={t("due")} htmlFor="iv-due" hint={t("dueHint")}>
            <Input id="iv-due" type="date" value={due} min={date || undefined} onChange={(e) => setDue(e.target.value)} />
          </Field>
        </div>
        <Field label={t("amount")} htmlFor="iv-amount" hint={t("amountHint")} error={errors.amount}>
          <Input id="iv-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-invalid={!!errors.amount} autoComplete="off" />
        </Field>
        {failure ? (
          <Alert variant="danger" data-testid="dialog-error">
            {failure}
          </Alert>
        ) : null}
        <Footer busy={act.isPending} confirm={t("confirm")} testId="invoice-confirm" />
      </form>
    </Shell>
  );
}

/* ---- Supplier payment ---------------------------------------------------------------------------------------------------- */

export function PayDialog({ order, onClose }: Shared) {
  const t = useTranslations("purchasing.dialogs.pay");
  const tm = useTranslations("purchasing.method");
  const tc = useTranslations("purchasing.common");
  const money = useMoney2();
  const perms = usePurchasingPermissions(order.branchId);
  const act = useOrderAction(order.id);
  const accounts = useAccounts({ branchId: order.branchId, enabled: perms.cashbookView });
  const { failure, setFailure, fail } = useFailure();
  const due = toNumber(order.outstanding);
  const [amount, setAmount] = React.useState(String(round2(due)));
  const [method, setMethod] = React.useState<string>("Cash");
  const [reference, setReference] = React.useState("");
  const [paidOn, setPaidOn] = React.useState(todayDhaka());
  const [accountId, setAccountId] = React.useState("");
  const [note, setNote] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);

  const accountOptions = React.useMemo(() => (accounts.data ?? []).filter((a) => a.isActive).map((a) => ({ value: a.id, label: `${a.name} · ${money(a.balance)}` })), [accounts.data, money]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const problem = paymentProblem(amount, due);
    setError(problem === "amount" ? t("amountInvalid") : problem === "exceeds" ? t("exceeds", { due: money(due) }) : null);
    if (problem) return;
    try {
      await act.mutateAsync({ kind: "pay", amount: round2(parseAmount(amount)!), method: method as (typeof MONEY_METHODS)[number], reference: reference.trim() || null, paidOn: paidOn || null, accountId: accountId || null, note: note.trim() || null });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      fail(e);
    }
  };

  return (
    <Shell title={t("title")} description={t("description", { number: order.number, due: money(due) })} onClose={onClose}>
      <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-4">
        <Field label={t("amount")} htmlFor="pay-amount" required error={error ?? undefined} hint={t("dueHint", { due: money(due) })}>
          <Input id="pay-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-invalid={!!error} autoComplete="off" />
        </Field>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("method")} htmlFor="pay-method">
            <Select id="pay-method" value={method} onValueChange={setMethod} options={MONEY_METHODS.map((m) => ({ value: m, label: tm(m) }))} placeholder={t("method")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
          <Field label={t("date")} htmlFor="pay-date">
            <Input id="pay-date" type="date" value={paidOn} onChange={(e) => setPaidOn(e.target.value)} />
          </Field>
        </div>
        {perms.cashbookView ? (
          <Field label={t("account")} htmlFor="pay-account" hint={t("accountHint")}>
            <Select id="pay-account" value={accountId || "__none"} onValueChange={(v) => setAccountId(v === "__none" ? "" : v)} options={[{ value: "__none", label: t("noAccount") }, ...accountOptions]} placeholder={t("account")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        ) : null}
        <Field label={t("reference")} htmlFor="pay-ref">
          <Input id="pay-ref" value={reference} maxLength={120} onChange={(e) => setReference(e.target.value)} autoComplete="off" />
        </Field>
        <Field label={t("note")} htmlFor="pay-note">
          <Textarea id="pay-note" rows={2} maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        {failure ? (
          <Alert variant="danger" data-testid="dialog-error">
            {failure}
          </Alert>
        ) : null}
        <Footer busy={act.isPending} confirm={t("confirm")} testId="pay-confirm" />
      </form>
    </Shell>
  );
}
