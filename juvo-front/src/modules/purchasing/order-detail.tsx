"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import { Banknote, FileText, PackageCheck, Pencil, Printer, Send, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert, Card, CardContent, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { AttachmentPicker, type UploadedFile } from "@/components/data/attachment-picker";
import { useDateTime } from "@/components/data/list-kit";
import { PageHeader } from "@/components/layout/page-header";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { VariantText } from "@/modules/inventory/common";
import { receiptProblem } from "@/modules/accounting/logic";
import { ActionDialog } from "@/modules/payments/action-dialog";
import { uploadPurchaseFile, useOrderAction, usePurchaseOrder, type PurchaseDetail } from "./api";
import { PayStatusBadge, PoStatusBadge, Stat, useDateOnly, useMoney2 } from "./common";
import { allowedActions } from "./logic";
import { usePurchasingPermissions } from "./permissions";

const ReceiveDialog = dynamic(() => import("./order-dialogs").then((m) => m.ReceiveDialog), { ssr: false });
const InvoiceDialog = dynamic(() => import("./order-dialogs").then((m) => m.InvoiceDialog), { ssr: false });
const PayDialog = dynamic(() => import("./order-dialogs").then((m) => m.PayDialog), { ssr: false });
const ReceiptDialog = dynamic(() => import("./receipt-dialog").then((m) => m.ReceiptDialog), { ssr: false });

type Dialog = null | "receive" | "invoice" | "pay" | "cancel" | "closeShort" | "send";

/** `/admin/purchasing/orders/[id]`: one purchase order with its actions, receipts, payments and history. */
export function OrderDetail({ id }: { id: string }) {
  const tc = useTranslations("purchasing.common");
  const query = usePurchaseOrder(id);
  if (query.isLoading) return <Skeleton className="h-96" aria-busy="true" />;
  if (query.isError || !query.data) return <Alert variant="danger">{tc("loadFailed")}</Alert>;
  return <Loaded order={query.data} />;
}

function Loaded({ order }: { order: PurchaseDetail }) {
  const t = useTranslations("purchasing.detail");
  const td = useTranslations("purchasing.dialogs");
  const tc = useTranslations("purchasing.common");
  const tn = useTranslations("nav");
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const dateTime = useDateTime();
  const report = useReportError();
  const perms = usePurchasingPermissions(order.branchId);
  const act = useOrderAction(order.id);
  const tm = useTranslations("purchasing.method");
  const tst = useTranslations("purchasing.status");
  const message = useProblemMessage();
  const statusLabel = (status: string) => (tst.has(status) ? tst(status) : status);
  const [dialog, setDialog] = React.useState<Dialog>(null);
  const [receiptId, setReceiptId] = React.useState<string | null>(null);
  const [added, setAdded] = React.useState<UploadedFile[]>([]);
  const close = () => setDialog(null);

  const actions = allowedActions(
    { status: order.status, amountPaid: toNumber(order.amountPaid), outstanding: toNumber(order.outstanding) },
    { edit: perms.poEdit, send: perms.poSend, receive: perms.poReceive, cancel: perms.poCancel, pay: perms.payablePay },
  );

  const run = async (action: Parameters<typeof act.mutateAsync>[0], done: string) => {
    try {
      await act.mutateAsync(action);
      toast.success(done);
      return null;
    } catch (e) {
      report(e);
      return null;
    }
  };

  /** Reason dialogs show the API's refusal inside the dialog. */
  const reasonSubmit = (kind: "cancel" | "closeShort") => async (values: Record<string, string>) => {
    try {
      await act.mutateAsync({ kind, reason: values.reason! });
      toast.success(kind === "cancel" ? td("cancel.done") : td("closeShort.done"));
      return null;
    } catch (e) {
      return message(e instanceof ApiError ? e.problem : null);
    }
  };

  const attach = async () => {
    await run({ kind: "attach", paths: added.map((f) => f.path!).filter(Boolean) }, t("attached"));
    setAdded([]);
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

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.purchasing"), href: "/admin/purchasing" }, { label: order.number }];
  const buttons = (
    <div className="flex flex-wrap gap-2">
      {actions.edit ? (
        <Button asChild variant="secondary">
          <Link href={`/admin/purchasing/orders/${order.id}/edit`} data-testid="po-edit">
            <Pencil aria-hidden /> {t("edit")}
          </Link>
        </Button>
      ) : null}
      {actions.send ? (
        <Button onClick={() => setDialog("send")} data-testid="po-send">
          <Send aria-hidden /> {t("send")}
        </Button>
      ) : null}
      {actions.receive ? (
        <Button onClick={() => setDialog("receive")} data-testid="po-receive">
          <PackageCheck aria-hidden /> {t("receive")}
        </Button>
      ) : null}
      {actions.closeShort ? (
        <Button variant="secondary" onClick={() => setDialog("closeShort")} data-testid="po-close-short">
          {t("closeShort")}
        </Button>
      ) : null}
      {actions.invoice ? (
        <Button variant="secondary" onClick={() => setDialog("invoice")} data-testid="po-invoice">
          <FileText aria-hidden /> {order.supplierInvoiceNumber ? t("editInvoice") : t("recordInvoice")}
        </Button>
      ) : null}
      {actions.pay ? (
        <Button variant="secondary" onClick={() => setDialog("pay")} data-testid="po-pay">
          <Banknote aria-hidden /> {t("pay")}
        </Button>
      ) : null}
      {actions.cancel ? (
        <Button variant="danger" onClick={() => setDialog("cancel")} data-testid="po-cancel">
          <XCircle aria-hidden /> {t("cancel")}
        </Button>
      ) : null}
      <Button asChild variant="secondary">
        <Link href={`/admin/purchasing/orders/${order.id}/print`} data-testid="po-print">
          <Printer aria-hidden /> {t("print")}
        </Link>
      </Button>
    </div>
  );

  return (
    <>
      <PageHeader title={t("title", { number: order.number })} description={t("subtitle", { supplier: order.supplierName, branch: order.branchCode })} crumbs={crumbs} actions={buttons} />
      <div className="space-y-6">
        <div className="flex flex-wrap items-center gap-2">
          <PoStatusBadge status={order.status} />
          <PayStatusBadge status={order.paymentStatus} overdue={order.isOverdue} />
        </div>

        {order.status === "Cancelled" && order.cancelReason ? <Alert variant="warning">{t("cancelledBecause", { reason: order.cancelReason })}</Alert> : null}

        <Card>
          <CardContent className="pt-5 sm:pt-6">
            <dl className="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-6" data-testid="po-summary">
              <Stat label={t("total")} value={money(order.total)} testId="sum-total" />
              <Stat label={t("receivedValue")} value={money(order.receivedValue)} testId="sum-received" />
              <Stat label={t("payable")} value={money(order.amountPayable)} testId="sum-payable" />
              <Stat label={t("paid")} value={money(order.amountPaid)} testId="sum-paid" />
              <Stat label={t("outstanding")} value={money(order.outstanding)} testId="sum-outstanding" emphasis />
              <Stat label={t("expected")} value={dateOnly(order.expectedDate)} />
            </dl>
            <p className="mt-3 text-sm text-muted" data-testid="balance-note">
              {toNumber(order.outstanding) > 0 ? t("balanceDue", { amount: money(order.outstanding) }) : t("balanceSettled")}
            </p>
            {order.supplierInvoiceNumber ? (
              <p className="mt-2 text-sm text-strong" data-testid="invoice-line">
                {t("invoiceLine", { number: order.supplierInvoiceNumber, date: dateOnly(order.supplierInvoiceDate), due: dateOnly(order.dueDate) })}
                {order.invoiceAmount !== null && order.invoiceAmount !== undefined ? ` · ${t("invoiceAmount", { amount: money(order.invoiceAmount) })}` : ""}
              </p>
            ) : null}
            {order.notes ? <p className="mt-2 text-sm text-muted [overflow-wrap:anywhere]">{order.notes}</p> : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("lines")}</CardTitle>
          </CardHeader>
          <CardContent>
            <ul className="divide-y divide-line" data-testid="po-lines">
              {order.lines.map((l) => (
                <li key={l.id} className="grid gap-2 py-3 lg:grid-cols-[1fr_auto] lg:items-center" data-sku={l.sku}>
                  <div className="min-w-0">
                    <p className="font-semibold text-strong [overflow-wrap:anywhere]">{l.productName}</p>
                    <VariantText sku={l.sku} colorName={l.colorName} size={l.size} />
                  </div>
                  <p className="text-sm tabular-nums text-muted" data-testid="line-qty">
                    {t("lineQty", { ordered: toNumber(l.orderedQuantity), received: toNumber(l.receivedQuantity), rejected: toNumber(l.rejectedQuantity) })}
                    {" · "}
                    {money(l.unitCost)} {" = "}
                    <span className="font-semibold text-strong">{money(l.lineTotal)}</span>
                  </p>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>

        <div className="grid gap-6 lg:grid-cols-2">
          <Card>
            <CardHeader>
              <CardTitle>{t("receipts")}</CardTitle>
            </CardHeader>
            <CardContent>
              {order.receipts.length === 0 ? (
                <p className="text-sm text-muted">{t("noReceipts")}</p>
              ) : (
                <ul className="divide-y divide-line" data-testid="po-receipts">
                  {order.receipts.map((r) => (
                    <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                      <div className="min-w-0">
                        <p className="font-mono text-sm font-semibold text-strong">{r.number}</p>
                        <p className="text-sm text-muted">
                          {dateTime(r.receivedAt)} · {t("receiptQty", { accepted: toNumber(r.acceptedQuantity), rejected: toNumber(r.rejectedQuantity) })}
                        </p>
                      </div>
                      {perms.receiptView ? (
                        <Button variant="secondary" onClick={() => setReceiptId(r.id)} aria-label={t("openReceipt", { number: r.number })}>
                          {t("view")}
                        </Button>
                      ) : null}
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("payments")}</CardTitle>
            </CardHeader>
            <CardContent>
              {order.payments.length === 0 ? (
                <p className="text-sm text-muted">{t("noPayments")}</p>
              ) : (
                <ul className="divide-y divide-line" data-testid="po-payments">
                  {order.payments.map((p) => (
                    <li key={p.id} className="py-2">
                      <p className="font-semibold tabular-nums text-strong">{money(p.amount)}</p>
                      <p className="text-sm text-muted [overflow-wrap:anywhere]">
                        {dateOnly(p.paidOn)} · {tm(p.method)}
                        {p.accountName ? ` · ${p.accountName}` : ""}
                        {p.reference ? ` · ${p.reference}` : ""}
                        {p.actorName ? ` · ${p.actorName}` : ""}
                      </p>
                    </li>
                  ))}
                </ul>
              )}
            </CardContent>
          </Card>
        </div>

        <Card>
          <CardHeader>
            <CardTitle>{t("attachments")}</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3">
            <AttachmentPicker idBase="po-att" saved={order.attachments} added={added} onAdded={setAdded} upload={(f) => uploadPurchaseFile(f)} check={receiptProblem} labels={attachLabels} onError={report} disabled={!actions.attach} />
            {added.length > 0 ? (
              <Button onClick={() => void attach()} loading={act.isPending}>
                {t("attachSave")}
              </Button>
            ) : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("history")}</CardTitle>
          </CardHeader>
          <CardContent>
            <ol className="space-y-3" data-testid="po-history">
              {order.history.map((h, i) => (
                <li key={`${h.at}-${i}`} className="text-sm">
                  <p className="font-semibold text-strong">{t("historyStep", { from: h.from ? statusLabel(h.from) : "-", to: statusLabel(h.to) })}</p>
                  <p className="text-muted [overflow-wrap:anywhere]">
                    {dateTime(h.at)}
                    {h.actorName ? ` · ${h.actorName}` : ""}
                    {h.reason ? ` · ${h.reason}` : ""}
                  </p>
                </li>
              ))}
            </ol>
          </CardContent>
        </Card>
      </div>

      <ConfirmDialog
        open={dialog === "send"}
        onOpenChange={(open) => !open && close()}
        title={td("send.title")}
        description={td("send.description", { number: order.number })}
        confirmLabel={td("send.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={act.isPending}
        onConfirm={async () => {
          await run({ kind: "send" }, td("send.done"));
          close();
        }}
      />
      <ActionDialog
        open={dialog === "cancel"}
        onOpenChange={(open) => !open && close()}
        title={td("cancel.title")}
        description={td("cancel.description", { number: order.number })}
        confirmLabel={td("cancel.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        destructive
        fields={[{ name: "reason", label: td("reason"), kind: "textarea", required: true, max: 300 }]}
        onSubmit={reasonSubmit("cancel")}
      />
      <ActionDialog
        open={dialog === "closeShort"}
        onOpenChange={(open) => !open && close()}
        title={td("closeShort.title")}
        description={td("closeShort.description", { number: order.number })}
        confirmLabel={td("closeShort.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        requiredText={tc("required")}
        fields={[{ name: "reason", label: td("reason"), kind: "textarea", required: true, max: 300 }]}
        onSubmit={reasonSubmit("closeShort")}
      />
      {dialog === "receive" ? <ReceiveDialog order={order} onClose={close} /> : null}
      {dialog === "invoice" ? <InvoiceDialog order={order} onClose={close} /> : null}
      {dialog === "pay" ? <PayDialog order={order} onClose={close} /> : null}
      {receiptId ? <ReceiptDialog id={receiptId} onClose={() => setReceiptId(null)} /> : null}
    </>
  );
}
