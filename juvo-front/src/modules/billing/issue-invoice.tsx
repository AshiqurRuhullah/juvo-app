"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { FileText } from "lucide-react";
import { Button } from "@/components/ui/button";
import { toast } from "@/components/ui/toaster";
import { usePaymentPermissions } from "@/modules/payments/permissions";
import { useIssueInvoice } from "./api";
import { useReportBillingError } from "./common";

/**
 * Issues (or fetches, when it exists already) the invoice of a confirmed order and then offers a link to it. Drawn only for people
 * who hold tax.invoice.issue in the order's branch; pass the order's branch id so the check matches the API's.
 */
export function IssueInvoiceButton({ orderId, branchId }: { orderId: string; branchId?: string }) {
  const t = useTranslations("billing.issue");
  const perms = usePaymentPermissions(branchId);
  const issue = useIssueInvoice();
  const report = useReportBillingError();
  if (!perms.invoiceIssue) return null;

  const run = async () => {
    try {
      const invoice = await issue.mutateAsync(orderId);
      toast.success(t("issued", { number: invoice.number }));
    } catch (error) {
      report(error);
    }
  };

  if (issue.data) {
    return (
      <Button asChild variant="secondary">
        <Link href={`/admin/billing/invoices/${issue.data.id}`} data-testid="open-invoice">
          <FileText aria-hidden /> {t("view")}
        </Link>
      </Button>
    );
  }
  return (
    <Button variant="secondary" onClick={() => void run()} loading={issue.isPending} data-testid="issue-invoice">
      <FileText aria-hidden /> {t("button")}
    </Button>
  );
}
