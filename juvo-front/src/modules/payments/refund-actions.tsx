"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Check, CircleX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { ActionDialog } from "./action-dialog";
import { useCompleteRefund, useFailRefund, type Refund } from "./api";
import { usePaymentErrorText } from "./common";

export function RefundStateBadge({ state }: { state: Refund["status"] }) {
  const t = useTranslations("payments.refundState");
  return <Badge variant={state === "Completed" ? "success" : state === "Failed" ? "danger" : "warning"}>{t(state)}</Badge>;
}

/** Complete or fail a Pending refund (the shop paid it by hand, or it did not happen). Both ask first; failing is the danger action. */
export function RefundActions({ refund, canApprove }: { refund: Refund; canApprove: boolean }) {
  const t = useTranslations("payments.admin.refund");
  const tc = useTranslations("payments.admin.common");
  const money = useMoney();
  const errorText = usePaymentErrorText();
  const complete = useCompleteRefund();
  const fail = useFailRefund();
  const [dialog, setDialog] = React.useState<"complete" | "fail" | null>(null);
  if (refund.status !== "Pending" || !canApprove) return null;

  return (
    <>
      <div className="flex flex-wrap gap-2">
        <Button variant="secondary" onClick={() => setDialog("complete")} data-testid="refund-complete">
          <Check aria-hidden /> {t("complete")}
        </Button>
        <Button variant="secondary" onClick={() => setDialog("fail")} data-testid="refund-fail">
          <CircleX aria-hidden /> {t("fail")}
        </Button>
      </div>
      <ActionDialog
        open={dialog === "complete"}
        onOpenChange={(o) => setDialog(o ? "complete" : null)}
        title={t("completeTitle", { amount: money(toNumber(refund.amount)) })}
        description={t("completeText", { number: refund.orderNumber })}
        confirmLabel={t("complete")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        fields={[{ name: "reference", label: t("reference"), hint: t("referenceHint"), max: 120 }]}
        onSubmit={async (v) => {
          try {
            await complete.mutateAsync({ refundId: refund.id, reference: v.reference || null });
            toast.success(t("completed"));
            return null;
          } catch (failure) {
            return errorText(failure);
          }
        }}
      />
      <ActionDialog
        open={dialog === "fail"}
        onOpenChange={(o) => setDialog(o ? "fail" : null)}
        title={t("failTitle")}
        description={t("failText")}
        confirmLabel={t("fail")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        destructive
        fields={[{ name: "reason", label: t("failReason"), kind: "textarea", required: true }]}
        onSubmit={async (v) => {
          try {
            await fail.mutateAsync({ refundId: refund.id, reason: v.reason! });
            toast.success(t("failed"));
            return null;
          } catch (failure) {
            return errorText(failure);
          }
        }}
      />
    </>
  );
}
