"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Check, FileText, Pencil, RotateCcw, Send, Trash2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert, Card, CardContent, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime } from "@/components/data/list-kit";
import { PageHeader } from "@/components/layout/page-header";
import { useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { mediaUrl } from "@/lib/media";
import { useReportError } from "@/modules/catalog/api";
import { ActionDialog } from "@/modules/payments/action-dialog";
import { Stat } from "@/modules/purchasing/common";
import { useExpense, useExpenseAction, type ExpenseDetail as Detail } from "./api";
import { ExpenseStatusBadge, isOwnSubmission, rememberSubmitted, useDateOnly, useMoney2 } from "./common";
import { expenseActions } from "./logic";
import { useAccountingPermissions } from "./permissions";

const ExpenseDialog = dynamic(() => import("./expense-dialog").then((m) => m.ExpenseDialog), { ssr: false });

type Dialog = null | "edit" | "reject" | "approve" | "delete";

/** `/admin/accounting/expenses/[id]`: one expense with its workflow steps (submit, approve, reject, return to draft, delete). */
export function ExpenseDetail({ id }: { id: string }) {
  const tc = useTranslations("accounting.common");
  const query = useExpense(id);
  if (query.isLoading) return <Skeleton className="h-80" aria-busy="true" />;
  if (query.isError || !query.data) return <Alert variant="danger">{tc("loadFailed")}</Alert>;
  return <Loaded expense={query.data} />;
}

function Loaded({ expense }: { expense: Detail }) {
  const t = useTranslations("accounting.detail");
  const td = useTranslations("accounting.dialogs");
  const tc = useTranslations("accounting.common");
  const tm = useTranslations("accounting.method");
  const tn = useTranslations("nav");
  const router = useRouter();
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const dateTime = useDateTime();
  const report = useReportError();
  const message = useProblemMessage();
  const { me } = useSession();
  const perms = useAccountingPermissions(expense.branchId);
  const act = useExpenseAction(expense.id);
  const [dialog, setDialog] = React.useState<Dialog>(null);
  const close = () => setDialog(null);
  const userId = me.staff?.id ?? null;
  // Only an expense this person is known to have sent is treated as their own (the API does not name the sender).
  const author = isOwnSubmission(userId, expense.id);
  const can = expenseActions(expense.status, { create: perms.expenseCreate, edit: perms.expenseEdit, delete: perms.expenseDelete, approve: perms.expenseApprove }, author);

  const run = async (action: Parameters<typeof act.mutateAsync>[0], done: string) => {
    try {
      await act.mutateAsync(action);
      toast.success(done);
      if (action.kind === "submit") rememberSubmitted(userId, expense.id);
      if (action.kind === "delete") router.push("/admin/accounting/expenses");
      return true;
    } catch (e) {
      report(e);
      return false;
    }
  };

  /** Dialog submit: null closes the dialog, text stays inside it. */
  const inDialog = (action: (values: Record<string, string>) => Parameters<typeof act.mutateAsync>[0], done: string) => async (values: Record<string, string>) => {
    try {
      await act.mutateAsync(action(values));
      toast.success(done);
      return null;
    } catch (e) {
      return message(e instanceof ApiError ? e.problem : null);
    }
  };

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.expenses"), href: "/admin/accounting/expenses" }, { label: expense.number }];
  const buttons = (
    <div className="flex flex-wrap gap-2">
      {can.edit ? (
        <Button variant="secondary" onClick={() => setDialog("edit")} data-testid="expense-edit">
          <Pencil aria-hidden /> {t("edit")}
        </Button>
      ) : null}
      {can.submit ? (
        <Button onClick={() => void run({ kind: "submit" }, t("submitted"))} loading={act.isPending} data-testid="expense-submit">
          <Send aria-hidden /> {t("submit")}
        </Button>
      ) : null}
      {can.approve ? (
        <Button onClick={() => setDialog("approve")} data-testid="expense-approve">
          <Check aria-hidden /> {t("approve")}
        </Button>
      ) : null}
      {can.reject ? (
        <Button variant="danger" onClick={() => setDialog("reject")} data-testid="expense-reject">
          <X aria-hidden /> {t("reject")}
        </Button>
      ) : null}
      {can.returnToDraft ? (
        <Button variant="secondary" onClick={() => void run({ kind: "returnToDraft" }, t("returned"))} loading={act.isPending} data-testid="expense-return">
          <RotateCcw aria-hidden /> {expense.status === "Rejected" ? t("reopen") : t("withdraw")}
        </Button>
      ) : null}
      {can.delete ? (
        <Button variant="danger" onClick={() => setDialog("delete")} data-testid="expense-delete">
          <Trash2 aria-hidden /> {t("delete")}
        </Button>
      ) : null}
    </div>
  );

  return (
    <>
      <PageHeader title={t("title", { number: expense.number })} description={t("subtitle", { category: expense.categoryName, branch: expense.branchCode })} crumbs={crumbs} actions={buttons} />
      <div className="space-y-6">
        <div className="flex flex-wrap items-center gap-2">
          <ExpenseStatusBadge status={expense.status} auto={expense.autoApproved} />
        </div>
        {expense.status === "Rejected" && expense.decisionNote ? <Alert variant="danger">{t("rejectedBecause", { reason: expense.decisionNote })}</Alert> : null}
        {expense.status === "Submitted" && author && perms.expenseApprove ? <Alert variant="info">{t("ownExpense")}</Alert> : null}

        <Card>
          <CardContent className="pt-5 sm:pt-6">
            <dl className="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-4" data-testid="expense-summary">
              <Stat label={t("amount")} value={money(expense.amount)} testId="expense-amount" emphasis />
              <Stat label={t("date")} value={dateOnly(expense.expenseDate)} />
              <Stat label={t("method")} value={tm(expense.method)} />
              <Stat label={t("account")} value={expense.accountName} />
              <Stat label={t("category")} value={expense.categoryName} />
              <Stat label={t("branch")} value={expense.branchCode} />
              {expense.supplierName ? <Stat label={t("supplier")} value={expense.supplierName} /> : null}
              {expense.reference ? <Stat label={t("reference")} value={expense.reference} /> : null}
            </dl>
            {expense.notes ? <p className="mt-3 text-sm text-muted [overflow-wrap:anywhere]">{expense.notes}</p> : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("receipts")}</CardTitle>
          </CardHeader>
          <CardContent>
            {expense.attachments.length === 0 ? (
              <p className="text-sm text-muted">{t("noReceipts")}</p>
            ) : (
              <ul className="divide-y divide-line rounded-control border border-line" data-testid="expense-receipts">
                {expense.attachments.map((a) => (
                  <li key={a.id} className="px-3">
                    <a href={mediaUrl(a.url)} target="_blank" rel="noopener noreferrer" className="inline-flex min-h-11 items-center gap-2 font-medium text-primary hover:underline" aria-label={t("openReceipt", { name: a.fileName })}>
                      <FileText className="size-4 shrink-0" aria-hidden />
                      <span className="[overflow-wrap:anywhere]">{a.fileName}</span>
                    </a>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("timeline")}</CardTitle>
          </CardHeader>
          <CardContent>
            <ol className="space-y-2 text-sm" data-testid="expense-timeline">
              <li>
                <span className="font-semibold text-strong">{t("created")}</span> <span className="text-muted">{dateTime(expense.createdAt)}</span>
              </li>
              {expense.submittedAt ? (
                <li>
                  <span className="font-semibold text-strong">{t("sent")}</span> <span className="text-muted">{dateTime(expense.submittedAt)}</span>
                </li>
              ) : null}
              {expense.decidedAt ? (
                <li>
                  <span className="font-semibold text-strong">{expense.status === "Rejected" ? t("rejectedBy", { name: expense.decidedByName ?? "-" }) : t("approvedBy", { name: expense.decidedByName ?? "-" })}</span>{" "}
                  <span className="text-muted">{dateTime(expense.decidedAt)}</span>
                  {expense.decisionNote ? <span className="block text-muted [overflow-wrap:anywhere]">{expense.decisionNote}</span> : null}
                </li>
              ) : null}
            </ol>
          </CardContent>
        </Card>
      </div>

      {dialog === "edit" ? <ExpenseDialog open onOpenChange={(open) => !open && close()} expense={expense} /> : null}
      <ActionDialog
        open={dialog === "approve"}
        onOpenChange={(open) => !open && close()}
        title={td("approve.title")}
        description={td("approve.description", { number: expense.number, amount: money(expense.amount) })}
        confirmLabel={td("approve.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        fields={[{ name: "note", label: td("approve.note"), kind: "textarea", max: 300 }]}
        onSubmit={inDialog((v) => ({ kind: "approve", note: v.note || undefined }), td("approve.done"))}
      />
      <ActionDialog
        open={dialog === "reject"}
        onOpenChange={(open) => !open && close()}
        title={td("reject.title")}
        description={td("reject.description", { number: expense.number })}
        confirmLabel={td("reject.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        destructive
        fields={[{ name: "reason", label: td("reject.reason"), kind: "textarea", required: true, max: 300 }]}
        onSubmit={inDialog((v) => ({ kind: "reject", reason: v.reason! }), td("reject.done"))}
      />
      <ConfirmDialog
        open={dialog === "delete"}
        onOpenChange={(open) => !open && close()}
        title={td("delete.title")}
        description={td("delete.description", { number: expense.number })}
        confirmLabel={td("delete.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={act.isPending}
        onConfirm={async () => {
          await run({ kind: "delete" }, td("delete.done"));
          close();
        }}
      />
    </>
  );
}
