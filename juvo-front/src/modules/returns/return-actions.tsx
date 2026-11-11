"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ChoiceRow, Field, Input, RadioGroup, RadioItem, Switch, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { canAny, canInBranch } from "@/lib/auth/permissions";
import { useProduct } from "@/modules/catalog/api";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useReturnAction, type ActionVars, type ReturnDetail, type ReturnItem } from "./api";
import { VariantLine } from "./common";
import {
  CONDITIONS,
  REFUND_METHODS,
  RETURN_FAULTS,
  allowedActions,
  defaultFault,
  defaultInspect,
  pickupBounds,
  restockAllowed,
  withCondition,
  type InspectDraft,
  type RefundMethod,
  type ReturnFault,
  type StageAction,
} from "./logic";

/** A dialog with a form: the submit button is the primary action, errors from the API show inside the dialog. */
function FormDialog({
  open,
  onOpenChange,
  title,
  description,
  submitLabel,
  destructive,
  loading,
  error,
  size = "md",
  onSubmit,
  children,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  submitLabel: string;
  destructive?: boolean;
  loading: boolean;
  error: string | null;
  size?: "sm" | "md" | "lg";
  onSubmit: () => void;
  children: React.ReactNode;
}) {
  const tc = useTranslations("returns.common");
  return (
    <Dialog open={open} onOpenChange={(next) => (loading ? undefined : onOpenChange(next))}>
      <ModalContent size={size} closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{title}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{description}</DialogDescription>
        <form
          noValidate
          className="mt-4 space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            onSubmit();
          }}
        >
          {children}
          {error ? (
            <Alert variant="danger" data-testid="action-problem">
              {error}
            </Alert>
          ) : null}
          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <DialogClose asChild>
              <Button variant="secondary" disabled={loading}>
                {tc("cancel")}
              </Button>
            </DialogClose>
            <Button type="submit" variant={destructive ? "danger" : "primary"} loading={loading}>
              {submitLabel}
            </Button>
          </div>
        </form>
      </ModalContent>
    </Dialog>
  );
}

/** The replacement variant of one exchange line. Staff with catalog access may change it; others see the one the customer asked for. */
function ExchangeLineRow({ item, value, onChange, canPick }: { item: ReturnItem; value: string; onChange: (variantId: string) => void; canPick: boolean }) {
  const t = useTranslations("returns.actions.exchange");
  const tc = useTranslations("returns.common");
  const product = useProduct(canPick ? item.productId : "");
  const variants = product.data?.variants.filter((v) => v.isActive) ?? [];
  const options = variants.map((v) => ({ value: v.id, label: [v.sku, v.colorName, v.size].filter(Boolean).join(" · ") }));
  const id = `ex-${item.id}`;
  return (
    <li className="space-y-2 py-3 first:pt-0 last:pb-0" data-testid="exchange-line">
      <p className="font-semibold text-strong [overflow-wrap:anywhere]">
        {item.name} × {toNumber(item.quantity)}
      </p>
      <VariantLine sku={item.sku} colorName={item.colorName} size={item.size} />
      {options.length > 1 ? (
        <Field label={t("replacement")} htmlFor={id}>
          <Select id={id} value={value} onValueChange={onChange} options={options} placeholder={t("replacement")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
      ) : (
        <p className="text-sm text-muted">{t("requested", { sku: item.exchangeSku ?? item.sku })}</p>
      )}
    </li>
  );
}

/**
 * The stage buttons of one return. Only moves the state machine allows now (the API's `nextStatuses`) AND the person's permission in the
 * return's own branch are shown; the API checks both again.
 */
export function ReturnActions({ detail }: { detail: ReturnDetail }) {
  const t = useTranslations("returns.actions");
  const tc = useTranslations("returns.common");
  const tf = useTranslations("returns.fault");
  const money = useMoney();
  const { me } = useSession();
  const problemMessage = useProblemMessage();
  const run = useReturnAction(detail.id);
  const [open, setOpen] = React.useState<StageAction | null>(null);
  const [error, setError] = React.useState<string | null>(null);

  const [note, setNote] = React.useState("");
  const [reason, setReason] = React.useState("");
  const [reasonError, setReasonError] = React.useState(false);
  const [date, setDate] = React.useState("");
  const [courier, setCourier] = React.useState("");
  const [drafts, setDrafts] = React.useState<Record<string, InspectDraft>>({});
  const [fault, setFault] = React.useState<ReturnFault>(defaultFault(detail.reasonCode));
  const [method, setMethod] = React.useState<RefundMethod>("OriginalPayment");
  const [reference, setReference] = React.useState("");
  const [referenceError, setReferenceError] = React.useState(false);
  const [dateError, setDateError] = React.useState(false);
  const [variantFor, setVariantFor] = React.useState<Record<string, string>>({});

  const can = (permission: string) => canInBranch(me, permission, detail.branchId);
  const actions = allowedActions(detail.nextStatuses, detail.kind, can);
  const bounds = pickupBounds();

  const reset = () => {
    setOpen(null);
    setError(null);
    setNote("");
    setReason("");
    setReasonError(false);
    setDate("");
    setCourier("");
    setReference("");
    setReferenceError(false);
    setDateError(false);
  };

  const send = async (vars: ActionVars, doneKey: string) => {
    setError(null);
    try {
      await run.mutateAsync(vars);
      toast.success(t(`done.${doneKey}`));
      reset();
    } catch (e) {
      const text = problemMessage(e instanceof ApiError ? e.problem : null);
      if (open) setError(text);
      else toast.error(text);
    }
  };

  const openAction = (action: StageAction) => {
    setError(null);
    if (action === "inspect") {
      setDrafts(Object.fromEntries(detail.items.map((i) => [i.id, defaultInspect()])));
      setFault(defaultFault(detail.reasonCode));
    }
    if (action === "refund") {
      setMethod(detail.order.paymentMethod === "COD" ? "Cash" : "OriginalPayment");
      setReference("");
    }
    if (action === "exchange") setVariantFor(Object.fromEntries(detail.items.map((i) => [i.id, i.exchangeVariantId ?? i.variantId])));
    setOpen(action);
  };

  if (actions.length === 0) return null;

  const accepted = detail.items.filter((i) => i.refundAccepted);
  const canPickVariant = canAny(me, ["catalog.product.view"], "anywhere");

  return (
    <>
      <div className="flex flex-wrap items-center gap-2" role="group" aria-label={t("label")} data-testid="return-actions">
        {actions.map((action) => {
          const primary = action === actions.find((a) => a !== "reject" && a !== "rejectRefund");
          const danger = action === "reject" || action === "rejectRefund";
          const direct = action === "pickedUp" || action === "received";
          return (
            <Button
              key={action}
              variant={primary ? "primary" : "secondary"}
              className={danger ? "border-danger/40 text-danger-ink" : undefined}
              loading={direct && run.isPending}
              onClick={() => (direct ? void send(action === "pickedUp" ? { action: "pickedUp" } : { action: "received" }, action) : openAction(action))}
              data-testid={`action-${action}`}
            >
              {t(`button.${action}`)}
            </Button>
          );
        })}
      </div>

      <ConfirmDialog
        open={open === "approve"}
        onOpenChange={(o) => !o && reset()}
        title={t("approve.title")}
        description={t("approve.description", { number: detail.number })}
        confirmLabel={t("approve.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={run.isPending}
        onConfirm={() => send({ action: "approve", note }, "approve")}
      >
        <Field label={t("noteLabel")} htmlFor="act-note" hint={t("noteHint")}>
          <Textarea id="act-note" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} className="min-h-20" />
        </Field>
        {error ? <Alert variant="danger" className="mt-3">{error}</Alert> : null}
      </ConfirmDialog>

      <ConfirmDialog
        open={open === "reject" || open === "rejectRefund"}
        onOpenChange={(o) => !o && reset()}
        title={open === "rejectRefund" ? t("rejectRefund.title") : t("reject.title")}
        description={open === "rejectRefund" ? t("rejectRefund.description") : t("reject.description")}
        confirmLabel={open === "rejectRefund" ? t("rejectRefund.confirm") : t("reject.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={run.isPending}
        onConfirm={() => {
          if (reason.trim() === "") {
            setReasonError(true);
            return;
          }
          return send(open === "rejectRefund" ? { action: "rejectRefund", reason } : { action: "reject", reason }, open === "rejectRefund" ? "rejectRefund" : "reject");
        }}
      >
        <Field label={t("reasonLabel")} htmlFor="act-reason" required hint={t("reasonHint")} error={reasonError ? t("reasonRequired") : undefined}>
          <Textarea id="act-reason" value={reason} maxLength={300} onChange={(e) => { setReason(e.target.value); setReasonError(false); }} className="min-h-20" />
        </Field>
        {error ? <Alert variant="danger" className="mt-3">{error}</Alert> : null}
      </ConfirmDialog>

      <FormDialog
        open={open === "schedulePickup"}
        onOpenChange={(o) => !o && reset()}
        title={t("schedulePickup.title")}
        description={t("schedulePickup.description")}
        submitLabel={t("schedulePickup.confirm")}
        loading={run.isPending}
        error={error}
        onSubmit={() => {
          if (!date) {
            setDateError(true);
            return;
          }
          void send({ action: "schedulePickup", date, courier, note }, "schedulePickup");
        }}
      >
        <Field label={t("schedulePickup.date")} htmlFor="act-date" required error={dateError ? t("schedulePickup.dateRequired") : undefined}>
          <Input id="act-date" type="date" value={date} min={bounds.min} max={bounds.max} onChange={(e) => { setDate(e.target.value); setDateError(false); }} />
        </Field>
        <Field label={t("schedulePickup.courier")} htmlFor="act-courier">
          <Input id="act-courier" value={courier} maxLength={80} onChange={(e) => setCourier(e.target.value)} autoComplete="off" />
        </Field>
        <Field label={t("schedulePickup.note")} htmlFor="act-pnote">
          <Textarea id="act-pnote" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} className="min-h-20" />
        </Field>
      </FormDialog>

      <FormDialog
        open={open === "inspect"}
        onOpenChange={(o) => !o && reset()}
        title={t("inspect.title")}
        description={t("inspect.description")}
        submitLabel={t("inspect.confirm")}
        loading={run.isPending}
        error={error}
        size="lg"
        onSubmit={() =>
          void send(
            {
              action: "inspect",
              body: {
                items: detail.items.map((i) => {
                  const d = drafts[i.id] ?? defaultInspect();
                  return { returnItemId: i.id, condition: d.condition, restock: d.restock, acceptRefund: d.acceptRefund };
                }),
                fault,
                note: note.trim() || null,
              },
            },
            "inspect",
          )
        }
      >
        <ul className="divide-y divide-line" aria-label={t("inspect.lines")}>
          {detail.items.map((item) => {
            const d = drafts[item.id] ?? defaultInspect();
            const set = (next: InspectDraft) => setDrafts((old) => ({ ...old, [item.id]: next }));
            return (
              <li key={item.id} className="space-y-3 py-4 first:pt-0 last:pb-0" data-testid="inspect-line">
                <div>
                  <p className="font-semibold text-strong [overflow-wrap:anywhere]">
                    {item.name} × {toNumber(item.quantity)}
                  </p>
                  <VariantLine sku={item.sku} colorName={item.colorName} size={item.size} />
                </div>
                <fieldset>
                  <legend className="mb-1 text-sm font-semibold text-strong">{t("inspect.condition")}</legend>
                  <RadioGroup value={d.condition} onValueChange={(v) => set(withCondition(d, v as InspectDraft["condition"]))} className="sm:grid-cols-3" aria-label={t("inspect.condition")}>
                    {CONDITIONS.map((c) => (
                      <ChoiceRow key={c} htmlFor={`cond-${item.id}-${c}`}>
                        <RadioItem id={`cond-${item.id}-${c}`} value={c} />
                        <span>{tc(`condition.${c}`)}</span>
                      </ChoiceRow>
                    ))}
                  </RadioGroup>
                </fieldset>
                <ChoiceRow htmlFor={`restock-${item.id}`}>
                  <Switch id={`restock-${item.id}`} checked={d.restock} disabled={!restockAllowed(d.condition)} onCheckedChange={(v) => set({ ...d, restock: v })} />
                  <span>
                    {t("inspect.restock")}
                    {!restockAllowed(d.condition) ? <span className="block text-sm text-muted">{t("inspect.damagedNoRestock")}</span> : null}
                  </span>
                </ChoiceRow>
                <ChoiceRow htmlFor={`accept-${item.id}`}>
                  <Switch id={`accept-${item.id}`} checked={d.acceptRefund} onCheckedChange={(v) => set({ ...d, acceptRefund: v })} />
                  <span>{detail.kind === "Exchange" ? t("inspect.acceptExchange") : t("inspect.acceptRefund")}</span>
                </ChoiceRow>
              </li>
            );
          })}
        </ul>
        <Field label={t("inspect.fault")} htmlFor="act-fault" hint={t("inspect.faultHint")}>
          <Select
            id="act-fault"
            value={fault}
            onValueChange={(v) => setFault(v as ReturnFault)}
            options={RETURN_FAULTS.map((f) => ({ value: f, label: tf(f) }))}
            placeholder={t("inspect.fault")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
        </Field>
        <Field label={t("inspect.note")} htmlFor="act-inote">
          <Textarea id="act-inote" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} className="min-h-20" />
        </Field>
      </FormDialog>

      <FormDialog
        open={open === "refund"}
        onOpenChange={(o) => !o && reset()}
        title={t("refund.title")}
        description={t("refund.description")}
        submitLabel={t("refund.confirm")}
        loading={run.isPending}
        error={error}
        onSubmit={() => {
          if (reference.trim() === "") {
            setReferenceError(true);
            return;
          }
          void send({ action: "refund", method, reference, amount: toNumber(detail.refund.total) }, "refund");
        }}
      >
        <Field label={t("refund.amount")} htmlFor="act-amount" hint={t("refund.amountHint")}>
          <Input id="act-amount" readOnly value={money(detail.refund.total)} className="bg-sunken font-semibold tabular-nums" data-testid="refund-amount" />
        </Field>
        <Field label={t("refund.method")} htmlFor="act-method">
          <Select
            id="act-method"
            value={method}
            onValueChange={(v) => setMethod(v as RefundMethod)}
            options={REFUND_METHODS.map((m) => ({ value: m, label: tc(`method.${m}`) }))}
            placeholder={t("refund.method")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
        </Field>
        <Field label={t("refund.reference")} htmlFor="act-reference" required hint={t("refund.referenceHint")} error={referenceError ? t("refund.referenceRequired") : undefined}>
          <Input id="act-reference" value={reference} maxLength={120} onChange={(e) => { setReference(e.target.value); setReferenceError(false); }} autoComplete="off" />
        </Field>
      </FormDialog>

      <FormDialog
        open={open === "exchange"}
        onOpenChange={(o) => !o && reset()}
        title={t("exchange.title")}
        description={t("exchange.description")}
        submitLabel={t("exchange.confirm")}
        loading={run.isPending}
        error={error}
        onSubmit={() => {
          const lines = accepted.filter((i) => variantFor[i.id] && variantFor[i.id] !== (i.exchangeVariantId ?? i.variantId)).map((i) => ({ returnItemId: i.id, variantId: variantFor[i.id]! }));
          void send({ action: "exchange", lines: lines.length > 0 ? lines : undefined, note }, "exchange");
        }}
      >
        <ul className="divide-y divide-line" aria-label={t("exchange.lines")}>
          {accepted.map((item) => (
            <ExchangeLineRow key={item.id} item={item} canPick={canPickVariant} value={variantFor[item.id] ?? item.exchangeVariantId ?? item.variantId} onChange={(v) => setVariantFor((old) => ({ ...old, [item.id]: v }))} />
          ))}
        </ul>
        <Field label={t("noteLabel")} htmlFor="act-xnote">
          <Textarea id="act-xnote" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} className="min-h-20" />
        </Field>
      </FormDialog>
    </>
  );
}
