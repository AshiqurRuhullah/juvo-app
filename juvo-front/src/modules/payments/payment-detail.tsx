"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Banknote, Check, CircleX, ExternalLink, Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, Badge, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { mediaUrl, toNumber, useMoney } from "@/modules/catalog/format";
import { PaymentStatusBadge, Section } from "@/modules/orders/common";
import { ActionDialog } from "./action-dialog";
import { useApprovePayment, useCodSettle, useCreateRefund, usePayment, useRejectPayment, type PaymentDetail as Detail } from "./api";
import { usePayMethodLabel, usePaymentErrorText } from "./common";
import { usePaymentPermissions } from "./permissions";
import { RefundActions, RefundStateBadge } from "./refund-actions";
import { actionSet } from "./status";

type Dialogs = "approve" | "reject" | "refund" | "settle" | null;

/** One payment for staff: facts, the proof of a transfer, the append-only event history, refunds, and the actions the server offers. */
export function PaymentDetail({ id }: { id: string }) {
  const t = useTranslations("payments.admin.detail");
  const tc = useTranslations("payments.admin.common");
  const tn = useTranslations("nav");
  const tk = useTranslations("payments.kind");
  const ts = useTranslations("orders.status.payment");
  const money = useMoney();
  const dateTime = useDateTime();
  const methodLabel = usePayMethodLabel();
  const errorText = usePaymentErrorText();
  const query = usePayment(id);
  const payment = query.data;
  const perms = usePaymentPermissions(payment?.branchId);
  const approve = useApprovePayment();
  const reject = useRejectPayment();
  const refund = useCreateRefund();
  const settle = useCodSettle();
  const [dialog, setDialog] = React.useState<Dialogs>(null);

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.payments"), href: "/admin/payments" }, { label: payment?.orderNumber ?? "..." }];

  if (query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy />
      </>
    );
  }
  if (query.isError || !payment) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFound") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/payments">{t("back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const actions = actionSet(payment.nextActions);
  const refundable = toNumber(payment.refundable);
  const proof = payment.proofUrl ? mediaUrl(payment.proofUrl) : null;
  const run = async <T,>(job: () => Promise<T>, done: string): Promise<string | null> => {
    try {
      await job();
      toast.success(t(`done.${done}`));
      return null;
    } catch (failure) {
      return errorText(failure);
    }
  };
  const cancel = tc("cancel");
  const close = tc("close");
  const required = tc("required");

  return (
    <>
      <PageHeader
        title={payment.orderNumber}
        description={t("subtitle", { kind: tk(payment.kind), method: methodLabel(payment.method) })}
        crumbs={crumbs}
        actions={
          <>
            <PaymentStatusBadge status={payment.status} />
            {actions.has("approve") && perms.verify ? (
              <Button onClick={() => setDialog("approve")} data-testid="action-approve">
                <Check aria-hidden /> {t("actions.approve")}
              </Button>
            ) : null}
            {actions.has("reject") && perms.verify ? (
              <Button variant="secondary" onClick={() => setDialog("reject")} data-testid="action-reject">
                <CircleX aria-hidden /> {t("actions.reject")}
              </Button>
            ) : null}
            {actions.has("settle") && perms.codSettle ? (
              <Button onClick={() => setDialog("settle")} data-testid="action-settle">
                <Banknote aria-hidden /> {t("actions.settle")}
              </Button>
            ) : null}
            {actions.has("refund") && perms.refundCreate ? (
              <Button variant="secondary" onClick={() => setDialog("refund")} data-testid="action-refund">
                <Undo2 aria-hidden /> {t("actions.refund")}
              </Button>
            ) : null}
            <Button asChild variant="secondary">
              <Link href={`/admin/orders/${payment.orderId}`} data-testid="open-order">
                <ExternalLink aria-hidden /> {t("openOrder")}
              </Link>
            </Button>
          </>
        }
      />

      <div className="space-y-5">
        {payment.status === "UnderReview" ? (
          <Alert variant="warning" title={t("review.title")} data-testid="review-banner">
            {t("review.text", { reference: payment.submittedReference ?? "-", payer: payment.payerName ?? "-" })}
          </Alert>
        ) : null}
        {payment.failureReason ? (
          <Alert variant="danger" title={t("failure")}>
            {payment.failureReason}
          </Alert>
        ) : null}

        <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,24rem)]">
          <div className="min-w-0 space-y-5">
            <Section title={t("history")} id="pd-events">
              <ol className="space-y-3" data-testid="payment-events">
                {payment.events.map((e, index) => (
                  <li key={`${e.at}-${index}`} className="flex items-start gap-3">
                    <span className="mt-1.5 size-2.5 shrink-0 rounded-full bg-primary" aria-hidden />
                    <div className="min-w-0">
                      <p className="font-medium text-strong">
                        {e.type}
                        {e.from || e.to ? (
                          <span className="font-normal text-muted">
                            {" "}
                            ({e.from ? `${ts(e.from)} → ` : ""}
                            {e.to ? ts(e.to) : ""})
                          </span>
                        ) : null}
                        {e.amount !== null && toNumber(e.amount) !== 0 ? <span className="ms-2 font-semibold tabular-nums">{money(e.amount)}</span> : null}
                      </p>
                      <p className="text-sm text-muted">
                        {dateTime(e.at, "long")}
                        {e.actor ? ` · ${e.actor}` : ""}
                      </p>
                      {e.detail ? <p className="text-sm text-muted [overflow-wrap:anywhere]">{e.detail}</p> : null}
                    </div>
                  </li>
                ))}
              </ol>
              <p className="mt-3 text-sm text-muted">{t("appendOnly")}</p>
            </Section>

            <Section title={t("refunds")} id="pd-refunds">
              {payment.refunds.length === 0 ? <p className="text-sm text-muted">{t("noRefunds")}</p> : null}
              <ul className="space-y-4" data-testid="payment-refunds">
                {payment.refunds.map((r) => (
                  <li key={r.id} className="space-y-2 rounded-control border border-line p-3">
                    <p className="flex flex-wrap items-center gap-2">
                      <strong className="tabular-nums text-strong">{money(r.amount)}</strong>
                      <RefundStateBadge state={r.status} />
                      <Badge variant="outline">{t(`mode.${r.mode}`)}</Badge>
                      {r.creditNoteNumber ? <Badge variant="info">{r.creditNoteNumber}</Badge> : null}
                    </p>
                    <p className="text-sm text-muted [overflow-wrap:anywhere]">
                      {dateTime(r.requestedAt)}
                      {r.requestedBy ? ` · ${r.requestedBy}` : ""}
                      {r.reason ? ` · ${r.reason}` : ""}
                      {r.reference ? ` · ${t("reference")}: ${r.reference}` : ""}
                    </p>
                    {r.failureReason ? <p className="text-sm font-medium text-danger">{r.failureReason}</p> : null}
                    <RefundActions refund={r} canApprove={perms.refundApprove} />
                  </li>
                ))}
              </ul>
            </Section>
          </div>

          <div className="min-w-0 space-y-5">
            <Section title={t("facts")} id="pd-facts">
              <Facts payment={payment} />
            </Section>
            {payment.submittedReference || payment.payerName || proof ? (
              <Section title={t("transfer")} id="pd-transfer">
                <dl className="space-y-1.5 text-sm">
                  <Fact label={t("fields.reference")} value={payment.submittedReference} testid="fact-reference" />
                  <Fact label={t("fields.payer")} value={payment.payerName} />
                </dl>
                {proof ? (
                  <div className="mt-3 space-y-2">
                    {proof.toLowerCase().endsWith(".pdf") ? null : (
                      // eslint-disable-next-line @next/next/no-img-element
                      <img src={proof} alt={t("proofAlt")} className="max-h-80 w-full rounded-control border border-line object-contain" />
                    )}
                    <a href={proof} target="_blank" rel="noreferrer" className="inline-flex min-h-11 items-center gap-2 font-semibold text-strong underline underline-offset-4" data-testid="proof-link">
                      <ExternalLink className="size-4" aria-hidden /> {t("openProof")}
                    </a>
                  </div>
                ) : (
                  <p className="mt-3 text-sm text-muted">{t("noProof")}</p>
                )}
              </Section>
            ) : null}
          </div>
        </div>
      </div>

      <ActionDialog
        open={dialog === "approve"}
        onOpenChange={(o) => setDialog(o ? "approve" : null)}
        title={t("approve.title")}
        description={t("approve.text", { amount: money(toNumber(payment.amount)), number: payment.orderNumber })}
        confirmLabel={t("actions.approve")}
        cancelLabel={cancel}
        closeLabel={close}
        requiredText={required}
        fields={[{ name: "note", label: t("approve.note"), kind: "textarea" }]}
        onSubmit={(v) => run(() => approve.mutateAsync({ id, note: v.note || null }), "approve")}
      />
      <ActionDialog
        open={dialog === "reject"}
        onOpenChange={(o) => setDialog(o ? "reject" : null)}
        title={t("reject.title")}
        description={t("reject.text")}
        confirmLabel={t("actions.reject")}
        cancelLabel={cancel}
        closeLabel={close}
        requiredText={required}
        destructive
        fields={[{ name: "reason", label: t("reject.reason"), kind: "textarea", required: true }]}
        onSubmit={(v) => run(() => reject.mutateAsync({ id, reason: v.reason! }), "reject")}
      />
      <ActionDialog
        open={dialog === "settle"}
        onOpenChange={(o) => setDialog(o ? "settle" : null)}
        title={t("settle.title")}
        description={t("settle.text", { amount: money(toNumber(payment.amount)) })}
        confirmLabel={t("actions.settle")}
        cancelLabel={cancel}
        closeLabel={close}
        requiredText={required}
        fields={[{ name: "reference", label: t("settle.reference"), hint: t("settle.hint"), required: true, max: 120 }]}
        onSubmit={(v) => run(() => settle.mutateAsync({ orderId: payment.orderId, reference: v.reference! }), "settle")}
      />
      <ActionDialog
        open={dialog === "refund"}
        onOpenChange={(o) => setDialog(o ? "refund" : null)}
        title={t("refund.title")}
        description={t("refund.text", { max: money(refundable) })}
        confirmLabel={t("refund.confirm")}
        cancelLabel={cancel}
        closeLabel={close}
        requiredText={required}
        destructive
        fields={[
          { name: "amount", label: t("refund.amount"), kind: "number", required: true, initial: String(refundable), hint: t("refund.amountHint", { max: money(refundable) }) },
          {
            name: "mode",
            label: t("refund.mode"),
            kind: "radio",
            initial: "Manual",
            options: [
              { value: "Manual", label: t("mode.Manual"), hint: t("refund.modeManual") },
              { value: "Gateway", label: t("mode.Gateway"), hint: t("refund.modeGateway") },
            ],
          },
          { name: "reason", label: t("refund.reason"), kind: "textarea", required: true },
        ]}
        onSubmit={async (v) => {
          const amount = Number(v.amount);
          if (!Number.isFinite(amount) || amount <= 0) return t("refund.amountInvalid");
          return run(() => refund.mutateAsync({ paymentId: id, amount, mode: v.mode === "Gateway" ? "Gateway" : "Manual", reason: v.reason! }), "refund");
        }}
      />
    </>
  );
}

function Fact({ label, value, testid }: { label: string; value: React.ReactNode; testid?: string }) {
  if (value === null || value === undefined || value === "") return null;
  return (
    <div className="flex justify-between gap-3">
      <dt className="text-muted">{label}</dt>
      <dd className="text-end font-medium text-strong [overflow-wrap:anywhere]" data-testid={testid}>
        {value}
      </dd>
    </div>
  );
}

function Facts({ payment }: { payment: Detail }) {
  const t = useTranslations("payments.admin.detail.fields");
  const tk = useTranslations("payments.kind");
  const money = useMoney();
  const dateTime = useDateTime();
  const methodLabel = usePayMethodLabel();
  return (
    <dl className="space-y-1.5 text-sm" data-testid="payment-facts">
      <Fact label={t("kind")} value={tk(payment.kind)} />
      <Fact label={t("method")} value={methodLabel(payment.method)} />
      <Fact label={t("provider")} value={payment.provider} />
      <Fact label={t("amount")} value={money(payment.amount)} testid="fact-amount" />
      <Fact label={t("refunded")} value={toNumber(payment.refundedAmount) > 0 ? money(payment.refundedAmount) : null} />
      <Fact label={t("refundPending")} value={toNumber(payment.refundPendingAmount) > 0 ? money(payment.refundPendingAmount) : null} />
      <Fact label={t("refundable")} value={toNumber(payment.refundable) > 0 ? money(payment.refundable) : null} testid="fact-refundable" />
      <Fact label={t("transaction")} value={payment.providerTransactionId} />
      <Fact label={t("received")} value={payment.receivedReference} />
      <Fact label={t("branch")} value={payment.branchCode} />
      <Fact label={t("created")} value={dateTime(payment.createdAt)} />
      <Fact label={t("paid")} value={payment.paidAt ? dateTime(payment.paidAt) : null} />
      <Fact label={t("settled")} value={payment.settledAt ? dateTime(payment.settledAt) : null} />
    </dl>
  );
}
