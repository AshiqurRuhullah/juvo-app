"use client";

import * as React from "react";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere, canInBranch } from "@/lib/auth/permissions";

/**
 * What this staff member may do with payments, refunds, invoices, tax rules and notifications. For one payment or order pass its
 * branch id (the API checks every action in the payment's own branch); for lists and settings leave it out (held in any branch).
 * The API still refuses what is not allowed; these only decide which buttons are drawn.
 */
export function usePaymentPermissions(branchId?: string) {
  const { me } = useSession();
  return React.useMemo(() => {
    const has = (code: string) => (branchId ? canInBranch(me, code, branchId) : canAnywhere(me, code));
    return {
      view: has("payments.payment.view"),
      verify: has("payments.payment.verify"),
      record: has("payments.payment.record"),
      refundView: has("payments.refund.view"),
      refundCreate: has("payments.refund.create"),
      refundApprove: has("payments.refund.approve"),
      ledger: has("payments.ledger.view"),
      gatewayView: has("payments.gateway.view"),
      gatewayEdit: has("payments.gateway.edit"),
      codSettle: has("couriers.cod.settle"),
      taxRuleView: has("tax.rule.view"),
      taxRuleEdit: has("tax.rule.edit"),
      invoiceView: has("tax.invoice.view"),
      invoiceDownload: has("tax.invoice.download"),
      invoiceIssue: has("tax.invoice.issue"),
      creditNoteView: has("tax.credit-note.view"),
      templateView: has("notifications.template.view"),
      templateEdit: has("notifications.template.edit"),
      logView: has("notifications.log.view"),
      logRetry: has("notifications.log.retry"),
      channelEdit: has("notifications.channel.edit"),
    };
  }, [me, branchId]);
}
