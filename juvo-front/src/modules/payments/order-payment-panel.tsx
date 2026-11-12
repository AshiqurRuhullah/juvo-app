"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Banknote, HandCoins, PiggyBank } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { IssueInvoiceButton } from "@/modules/billing/issue-invoice";
import { PaymentStatusBadge, Section } from "@/modules/orders/common";
import { ActionDialog } from "./action-dialog";
import { useCodCollect, useCodSettle, useOrderPayments, useRecordAdvance } from "./api";
import { usePayMethodLabel, usePaymentErrorText } from "./common";
import { usePaymentPermissions } from "./permissions";

type Order = { id: string; number: string; branchId: string; status: string; paymentStatus: string; paymentMethod: string; confirmedAt: string | null; money: { grandTotal: number | string; advanceRequired: number | string } };
type Dialogs = "advance" | "collect" | "settle" | null;

/**
 * The payment part of the staff order page: the order's payments, the cash-on-delivery steps (advance before packing, cash
 * collected, cash handed over) and the invoice. Every button is drawn only when the person may do it in the order's own branch and
 * the order is in a state where the API would allow it; the API checks again.
 */
export function OrderPaymentPanel({ order }: { order: Order }) {
  const t = useTranslations("payments.admin.orderPanel");
  const tk = useTranslations("payments.kind");
  const tc = useTranslations("payments.admin.common");
  const money = useMoney();
  const methodLabel = usePayMethodLabel();
  const errorText = usePaymentErrorText();
  const perms = usePaymentPermissions(order.branchId);
  const payments = useOrderPayments(order.id, perms.view);
  const advance = useRecordAdvance();
  const collect = useCodCollect();
  const settle = useCodSettle();
  const [dialog, setDialog] = React.useState<Dialogs>(null);
  if (!perms.view && !perms.invoiceIssue) return null;

  const list = payments.data ?? [];
  const cod = order.paymentMethod === "COD";
  const required = toNumber(order.money.advanceRequired);
  const paidAdvance = list.filter((p) => p.kind === "Advance" && p.status === "Paid").reduce((sum, p) => sum + toNumber(p.amount), 0);
  const advanceLeft = Math.max(0, required - paidAdvance);
  const canAdvance = cod && advanceLeft > 0 && ["Placed", "Confirmed"].includes(order.status) && perms.record;
  const canCollect = cod && order.paymentStatus === "Pending" && ["OutForDelivery", "Delivered"].includes(order.status) && perms.record;
  const canSettle = cod && order.paymentStatus === "CollectedByCourier" && perms.codSettle;
  const total = toNumber(order.money.grandTotal);

  const run = async <T,>(job: () => Promise<T>, done: string): Promise<string | null> => {
    try {
      await job();
      toast.success(t(`done.${done}`));
      return null;
    } catch (failure) {
      return errorText(failure);
    }
  };

  return (
    <Section title={t("title")} id="od-payments" className="space-y-4">
      {cod && required > 0 ? (
        <Alert variant={advanceLeft > 0 ? "warning" : "success"} data-testid="advance-state">
          {advanceLeft > 0 ? t("advanceDue", { left: money(advanceLeft), required: money(required) }) : t("advanceDone", { required: money(required) })}
        </Alert>
      ) : null}

      {perms.view ? (
        list.length === 0 ? (
          <p className="text-sm text-muted">{cod ? t("noneCod") : t("none")}</p>
        ) : (
          <ul className="space-y-2" data-testid="order-payments">
            {list.map((p) => (
              <li key={p.id} className="flex flex-wrap items-center justify-between gap-2 rounded-control border border-line p-3">
                <div className="min-w-0 space-y-1">
                  <p className="flex flex-wrap items-center gap-2 text-sm">
                    <Badge variant="outline">{tk(p.kind)}</Badge>
                    <span className="text-strong">{methodLabel(p.method)}</span>
                    <strong className="tabular-nums text-strong">{money(p.amount)}</strong>
                  </p>
                  <PaymentStatusBadge status={p.status} />
                </div>
                <Button asChild variant="secondary" size="md">
                  <Link href={`/admin/payments/${p.id}`} data-testid="open-payment">
                    {t("open")}
                  </Link>
                </Button>
              </li>
            ))}
          </ul>
        )
      ) : null}

      <div className="flex flex-wrap gap-2">
        {canAdvance ? (
          <Button variant="secondary" onClick={() => setDialog("advance")} data-testid="record-advance">
            <PiggyBank aria-hidden /> {t("recordAdvance")}
          </Button>
        ) : null}
        {canCollect ? (
          <Button variant="secondary" onClick={() => setDialog("collect")} data-testid="cod-collect">
            <HandCoins aria-hidden /> {t("collect")}
          </Button>
        ) : null}
        {canSettle ? (
          <Button variant="secondary" onClick={() => setDialog("settle")} data-testid="cod-settle">
            <Banknote aria-hidden /> {t("settle")}
          </Button>
        ) : null}
        {order.confirmedAt ? <IssueInvoiceButton orderId={order.id} branchId={order.branchId} /> : null}
      </div>

      <ActionDialog
        open={dialog === "advance"}
        onOpenChange={(o) => setDialog(o ? "advance" : null)}
        title={t("advance.title")}
        description={t("advance.text", { left: money(advanceLeft) })}
        confirmLabel={t("recordAdvance")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        fields={[
          { name: "amount", label: t("advance.amount"), kind: "number", required: true, initial: String(advanceLeft) },
          { name: "method", label: t("advance.method"), hint: t("advance.methodHint"), initial: "CASH", required: true, max: 30 },
          { name: "reference", label: t("advance.reference"), max: 120 },
        ]}
        onSubmit={async (v) => {
          const amount = Number(v.amount);
          if (!Number.isFinite(amount) || amount <= 0) return t("advance.amountInvalid");
          return run(() => advance.mutateAsync({ orderId: order.id, amount, method: v.method!.toUpperCase(), reference: v.reference || null }), "advance");
        }}
      />
      <ActionDialog
        open={dialog === "collect"}
        onOpenChange={(o) => setDialog(o ? "collect" : null)}
        title={t("collectDialog.title")}
        description={t("collectDialog.text", { total: money(total) })}
        confirmLabel={t("collect")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        fields={[{ name: "reference", label: t("collectDialog.reference"), hint: t("collectDialog.hint"), max: 120 }]}
        onSubmit={(v) => run(() => collect.mutateAsync({ orderId: order.id, amount: null, reference: v.reference || null }), "collect")}
      />
      <ActionDialog
        open={dialog === "settle"}
        onOpenChange={(o) => setDialog(o ? "settle" : null)}
        title={t("settleDialog.title")}
        description={t("settleDialog.text")}
        confirmLabel={t("settle")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        fields={[{ name: "reference", label: t("settleDialog.reference"), hint: t("settleDialog.hint"), required: true, max: 120 }]}
        onSubmit={(v) => run(() => settle.mutateAsync({ orderId: order.id, reference: v.reference! }), "settle")}
      />
    </Section>
  );
}
