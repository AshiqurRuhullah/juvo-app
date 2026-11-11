"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { usePaymentPermissions } from "@/modules/payments/permissions";
import { usePickName } from "@/modules/store/hooks";
import { AmountList, PaymentStatusBadge, Section, useMethodLabel } from "@/modules/orders/common";
import { creditNotePdfUrl, invoicePdfUrl, useCreditNote, useInvoice } from "./api";
import { isNotFound, PdfLinks } from "./common";

function Missing({ title, text, tn, back }: { title: string; text: string; tn: string; back: string }) {
  return (
    <>
      <PageHeader title={title} crumbs={[{ label: tn, href: "/admin/billing" }, { label: title }]} />
      <EmptyState
        title={title}
        description={text}
        action={
          <Button asChild variant="secondary">
            <Link href="/admin/billing">{back}</Link>
          </Button>
        }
      />
    </>
  );
}

/** One invoice: seller, customer, items with tax, totals, the credit notes made from it and the PDF in both languages. */
export function InvoiceDetail({ id }: { id: string }) {
  const t = useTranslations("billing.invoice");
  const tc = useTranslations("billing.common");
  const tn = useTranslations("nav");
  const money = useMoney();
  const dateTime = useDateTime();
  const pick = usePickName();
  const methodLabel = useMethodLabel();
  const perms = usePaymentPermissions();
  const query = useInvoice(id);
  const invoice = query.data;
  const root = tn("items.taxInvoices");

  if (query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={[{ label: root, href: "/admin/billing" }]} />
        <Skeleton className="h-64" aria-busy />
      </>
    );
  }
  if (query.isError || !invoice) {
    return isNotFound(query.error) ? <Missing title={t("notFound")} text={t("notFoundText")} tn={root} back={tc("back")} /> : (
      <>
        <PageHeader title={t("title")} crumbs={[{ label: root, href: "/admin/billing" }]} />
        <Alert variant="danger">{tc("loadFailed")}</Alert>
      </>
    );
  }

  const rate = invoice.taxRatePercent === null || invoice.taxRatePercent === undefined ? null : toNumber(invoice.taxRatePercent);
  return (
    <>
      <PageHeader
        title={invoice.number}
        description={t("issuedOn", { date: dateTime(invoice.issuedAt, "long") })}
        crumbs={[{ label: root, href: "/admin/billing" }, { label: invoice.number }]}
        actions={perms.invoiceDownload ? <PdfLinks href={(lang) => invoicePdfUrl(invoice.id, lang)} testId="invoice-pdf" /> : null}
      />
      <div className="space-y-5">
        <div className="grid gap-5 lg:grid-cols-3">
          <Section title={t("seller")} id="inv-seller">
            <address className="not-italic text-strong [overflow-wrap:anywhere]">
              <span className="block font-semibold">{invoice.seller.name}</span>
              {[invoice.seller.address, invoice.seller.phone, invoice.seller.email].filter(Boolean).map((line) => (
                <span key={line} className="block text-muted">
                  {line}
                </span>
              ))}
              {invoice.seller.taxId ? <span className="block text-muted">{t("taxId", { id: invoice.seller.taxId })}</span> : null}
            </address>
          </Section>
          <Section title={t("customer")} id="inv-customer">
            <address className="not-italic text-strong [overflow-wrap:anywhere]">
              <span className="block font-semibold">{invoice.customerName}</span>
              <span className="block text-muted">{invoice.customerMobile}</span>
              {invoice.customerEmail ? <span className="block text-muted">{invoice.customerEmail}</span> : null}
              <span className="mt-2 block text-sm text-muted">{t("billingAddress")}</span>
              <span className="block">{invoice.billingAddress}</span>
            </address>
          </Section>
          <Section title={t("order")} id="inv-order">
            <p className="font-mono font-semibold text-strong [overflow-wrap:anywhere]">{invoice.orderNumber}</p>
            <p className="mt-3 text-sm text-muted">{t("payment")}</p>
            <p className="flex flex-wrap items-center gap-2 text-strong">
              {methodLabel(invoice.paymentMethod)} <PaymentStatusBadge status={invoice.paymentStatus} />
            </p>
          </Section>
        </div>

        <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,24rem)]">
          <Section title={t("lines")} id="inv-lines">
            <ul className="divide-y divide-line" aria-label={t("lines")} data-testid="invoice-lines">
              {invoice.lines.map((line, index) => (
                <li key={`${line.sku}-${index}`} className="flex gap-3 py-3" data-sku={line.sku}>
                  <div className="min-w-0 flex-1">
                    <p className="font-medium text-strong [overflow-wrap:anywhere]">{pick(line.name, line.nameBn)}</p>
                    <p className="text-sm text-muted [overflow-wrap:anywhere]">{[line.sku, line.variant].filter(Boolean).join(" · ")}</p>
                    <p className="text-sm text-muted">
                      {money(line.unitPrice)} × {toNumber(line.quantity)}
                      {toNumber(line.taxAmount) > 0 ? ` · ${t("columns.tax")} ${money(line.taxAmount)}` : ""}
                    </p>
                  </div>
                  <p className="shrink-0 font-semibold text-strong">{money(line.lineTotal)}</p>
                </li>
              ))}
            </ul>
          </Section>
          <div className="space-y-5">
            <Section title={t("grandTotal")} id="inv-totals">
              <AmountList
                rows={[
                  { key: "subtotal", label: t("subtotal"), value: invoice.subtotal, always: true },
                  { key: "discount", label: t("discount"), value: invoice.discount, negative: true },
                  { key: "delivery", label: t("delivery"), value: invoice.deliveryCharge, always: true },
                  { key: "total", label: t("grandTotal"), value: invoice.grandTotal, strong: true },
                  { key: "tax", label: t("taxTotal"), value: invoice.taxTotal, always: !!invoice.taxRuleCode },
                  { key: "net", label: t("netTotal"), value: invoice.netTotal, always: !!invoice.taxRuleCode },
                  { key: "credited", label: t("credited"), value: invoice.creditedTotal },
                ]}
              />
              <p className="mt-3 text-sm text-muted" data-testid="invoice-tax-note">
                {invoice.taxRuleCode ? t("taxRule", { name: invoice.taxRuleName ?? invoice.taxRuleCode, rate: rate ?? 0 }) : t("noTax")}
              </p>
            </Section>
            <Section title={t("creditNotes")} id="inv-credits">
              {invoice.creditNotes.length === 0 ? <p className="text-sm text-muted">{t("noCreditNotes")}</p> : null}
              <ul className="space-y-2">
                {invoice.creditNotes.map((note) => (
                  <li key={note.id} className="flex items-center justify-between gap-3">
                    <Link href={`/admin/billing/credit-notes/${note.id}`} className="inline-flex min-h-11 items-center font-mono font-semibold text-strong hover:underline">
                      {note.number}
                    </Link>
                    <span className="tabular-nums">{money(note.amount)}</span>
                  </li>
                ))}
              </ul>
            </Section>
          </div>
        </div>
      </div>
    </>
  );
}

/** One credit note with its PDF. */
export function CreditNoteDetail({ id }: { id: string }) {
  const t = useTranslations("billing.creditNote");
  const tc = useTranslations("billing.common");
  const tn = useTranslations("nav");
  const money = useMoney();
  const dateTime = useDateTime();
  const perms = usePaymentPermissions();
  const query = useCreditNote(id);
  const note = query.data;
  const root = tn("items.taxInvoices");

  if (query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={[{ label: root, href: "/admin/billing" }]} />
        <Skeleton className="h-48" aria-busy />
      </>
    );
  }
  if (query.isError || !note) {
    return isNotFound(query.error) ? <Missing title={t("notFound")} text={t("notFoundText")} tn={root} back={tc("back")} /> : (
      <>
        <PageHeader title={t("title")} crumbs={[{ label: root, href: "/admin/billing" }]} />
        <Alert variant="danger">{tc("loadFailed")}</Alert>
      </>
    );
  }
  return (
    <>
      <PageHeader
        title={note.number}
        description={t("issuedOn", { date: dateTime(note.issuedAt, "long") })}
        crumbs={[{ label: root, href: "/admin/billing?tab=creditNotes" }, { label: note.number }]}
        actions={perms.creditNoteView ? <PdfLinks href={(lang) => creditNotePdfUrl(note.id, lang)} testId="credit-note-pdf" /> : null}
      />
      <Section title={t("title")} id="cn-main">
        <dl className="grid gap-4 text-sm sm:grid-cols-2">
          <div>
            <dt className="text-muted">{t("invoice")}</dt>
            <dd>
              <Link href={`/admin/billing/invoices/${note.invoiceId}`} className="inline-flex min-h-11 items-center font-mono font-semibold text-strong hover:underline">
                {note.invoiceNumber}
              </Link>
            </dd>
          </div>
          <div>
            <dt className="text-muted">{t("order")}</dt>
            <dd className="font-mono font-semibold text-strong">{note.orderNumber}</dd>
          </div>
          <div>
            <dt className="text-muted">{t("amount")}</dt>
            <dd className="text-lg font-semibold text-strong">{money(note.amount)}</dd>
          </div>
          <div>
            <dt className="text-muted">{t("tax")}</dt>
            <dd className="text-strong">{money(note.taxAmount)}</dd>
          </div>
          <div className="sm:col-span-2">
            <dt className="text-muted">{t("reason")}</dt>
            <dd className="text-strong [overflow-wrap:anywhere]">{note.reason ?? t("noReason")}</dd>
          </div>
        </dl>
      </Section>
    </>
  );
}
