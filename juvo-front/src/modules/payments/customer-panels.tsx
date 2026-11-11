"use client";

import * as React from "react";
import Link from "next/link";
import { useLocale, useTranslations } from "next-intl";
import { ChevronDown, ChevronUp, Download, FileText, Hourglass, Wallet } from "lucide-react";
import { buttonVariants } from "@/components/ui/button";
import { Alert } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { usePickName } from "@/modules/store/hooks";
import { Section, TrackHistory } from "@/modules/orders/common";
import type { TimelineEntry } from "@/modules/orders/api";
import { guestInvoicePdfUrl, myInvoicePdfUrl, useGuestInvoice, useMyInvoice, type Invoice } from "@/modules/billing/api";
import { useTracking } from "@/modules/orders/api";
import { usePayMethodLabel } from "./common";
import { useOrderAccess } from "./order-access";
import { needsPayment, payPath } from "./status";
import type { PaymentStatus } from "./api";

/**
 * The payment part of an order page: what to do when payment is still due ("Pay now"), a note while a transfer is under review,
 * and a short word when a payment failed. Shown on the confirmation, tracking and my-order pages.
 */
export function PaymentPanel({ number, method, paymentStatus, orderStatus, token }: { number: string; method: string; paymentStatus: PaymentStatus; orderStatus: string; token?: string | null }) {
  const t = useTranslations("payments.panel");
  const label = usePayMethodLabel();
  if (method === "COD") return null;
  const open = needsPayment(method, paymentStatus, orderStatus);
  if (!open && paymentStatus !== "UnderReview") return null;

  if (paymentStatus === "UnderReview") {
    return (
      <Alert variant="info" title={t("reviewTitle")} data-testid="payment-review">
        <Hourglass className="me-1 inline size-4" aria-hidden /> {t("reviewText")}
      </Alert>
    );
  }
  const failed = paymentStatus === "Failed" || paymentStatus === "Cancelled";
  return (
    <div className={cn("space-y-3 rounded-card border p-4", failed ? "border-danger/40 bg-danger-soft" : "border-line bg-primary-soft")} data-testid="payment-due">
      <p className="flex items-start gap-2 font-semibold text-strong">
        <Wallet className="mt-0.5 size-5 shrink-0 text-primary" aria-hidden />
        <span>{failed ? t(paymentStatus === "Failed" ? "failed" : "cancelled") : t("due", { method: label(method) })}</span>
      </p>
      <Link href={payPath(number, token, failed)} className={buttonVariants({ size: "lg", className: "w-full sm:w-auto" })} data-testid="pay-now">
        {failed ? t("tryAgain") : method === "MANUAL" ? t("sendDetails") : t("payNow")}
      </Link>
    </div>
  );
}

/** The panel for an order this browser can prove (token kept from checkout, or signed in): reads the live status, so a paid order stops asking for money. */
export function LivePaymentPanel({ number }: { number: string }) {
  const access = useOrderAccess(number);
  const tracking = useTracking(access.query);
  const order = tracking.data;
  if (!order) return null;
  return <PaymentPanel number={number} method={order.paymentMethod} paymentStatus={order.paymentStatus} orderStatus={order.status} token={access.token} />;
}

/** Payment history of an order for the customer: the payment track (what became what, when). The API has no list of payments for customers. */
export function PaymentHistory({ timeline, id }: { timeline: TimelineEntry[]; id: string }) {
  const t = useTranslations("payments.panel");
  return (
    <Section title={t("history")} id={id}>
      <TrackHistory timeline={timeline} track="Payment" />
    </Section>
  );
}

function InvoiceBody({ invoice, href }: { invoice: Invoice; href: (lang: "en" | "bn") => string }) {
  const t = useTranslations("payments.invoice");
  const money = useMoney();
  const dateTime = useDateTime();
  const pick = usePickName();
  const locale = useLocale();
  const [open, setOpen] = React.useState(false);
  const tax = toNumber(invoice.taxTotal);
  return (
    <div className="space-y-3" data-testid="invoice-box">
      <p className="text-sm text-muted">
        {t("number")}: <strong className="font-mono text-strong" data-testid="invoice-number">{invoice.number}</strong> · {t("issued", { date: dateTime(invoice.issuedAt) })}
      </p>
      <div className="flex flex-wrap gap-2">
        {(locale === "bn" ? (["bn", "en"] as const) : (["en", "bn"] as const))
          .map((lang) => (
            <a key={lang} href={href(lang)} download className={buttonVariants({ variant: lang === locale ? "primary" : "secondary" })} data-testid={`invoice-pdf-${lang}`}>
              <Download aria-hidden /> {t(lang === "en" ? "downloadEn" : "downloadBn")}
            </a>
          ))}
        <button type="button" onClick={() => setOpen((v) => !v)} aria-expanded={open} className={buttonVariants({ variant: "ghost" })} data-testid="invoice-toggle">
          {open ? <ChevronUp aria-hidden /> : <ChevronDown aria-hidden />} {open ? t("hide") : t("view")}
        </button>
      </div>
      {open ? (
        <div className="space-y-3 rounded-control border border-line p-3" data-testid="invoice-data">
          <ul className="divide-y divide-line text-sm">
            {invoice.lines.map((line) => (
              <li key={`${line.sku}-${line.variant ?? ""}`} className="flex justify-between gap-3 py-2">
                <span className="min-w-0 [overflow-wrap:anywhere]">
                  <span className="font-medium text-strong">{pick(line.name, line.nameBn)}</span>
                  <span className="block text-muted">
                    {[line.sku, line.variant].filter(Boolean).join(" · ")} · {money(line.unitPrice)} × {toNumber(line.quantity)}
                  </span>
                </span>
                <span className="shrink-0 font-semibold text-strong">{money(line.lineTotal)}</span>
              </li>
            ))}
          </ul>
          <dl className="space-y-1 border-t border-line pt-2 text-sm">
            <div className="flex justify-between gap-3">
              <dt className="text-muted">{t("subtotal")}</dt>
              <dd className="text-strong">{money(invoice.subtotal)}</dd>
            </div>
            {toNumber(invoice.discount) !== 0 ? (
              <div className="flex justify-between gap-3">
                <dt className="text-muted">{t("discount")}</dt>
                <dd className="text-strong">-{money(invoice.discount)}</dd>
              </div>
            ) : null}
            <div className="flex justify-between gap-3">
              <dt className="text-muted">{t("delivery")}</dt>
              <dd className="text-strong">{money(invoice.deliveryCharge)}</dd>
            </div>
            {tax > 0 ? (
              <div className="flex justify-between gap-3">
                <dt className="text-muted">{t("taxIncluded", { rate: invoice.taxRatePercent === null ? "" : `${toNumber(invoice.taxRatePercent)}%` })}</dt>
                <dd className="text-strong">{money(invoice.taxTotal)}</dd>
              </div>
            ) : null}
            <div className="flex justify-between gap-3 border-t border-line pt-2 text-base font-semibold">
              <dt className="text-strong">{t("total")}</dt>
              <dd className="text-strong" data-testid="invoice-total">{money(invoice.grandTotal)}</dd>
            </div>
          </dl>
        </div>
      ) : null}
    </div>
  );
}

/** Shown when the order has an invoice; nothing otherwise (an order that is not confirmed yet has none). */
export function MyInvoicePanel({ orderId, id }: { orderId: string; id: string }) {
  const t = useTranslations("payments.invoice");
  const invoice = useMyInvoice(orderId, true);
  if (!invoice.data) return null;
  return (
    <Section title={t("title")} id={id}>
      <p className="mb-2 flex items-center gap-2 text-sm text-muted">
        <FileText className="size-4" aria-hidden /> {t("help")}
      </p>
      <InvoiceBody invoice={invoice.data} href={(lang) => myInvoicePdfUrl(orderId, lang)} />
    </Section>
  );
}

/** The same for someone tracking with the private link: the tracking token proves the order. */
export function GuestInvoicePanel({ number, token, id }: { number: string; token: string | null; id: string }) {
  const t = useTranslations("payments.invoice");
  const invoice = useGuestInvoice(number, token, true);
  if (!token || !invoice.data) return null;
  return (
    <Section title={t("title")} id={id}>
      <p className="mb-2 flex items-center gap-2 text-sm text-muted">
        <FileText className="size-4" aria-hidden /> {t("help")}
      </p>
      <InvoiceBody invoice={invoice.data} href={(lang) => guestInvoicePdfUrl(number, token, lang)} />
    </Section>
  );
}
