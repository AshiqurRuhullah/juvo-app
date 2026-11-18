"use client";

import * as React from "react";
import { useLocale, useTranslations } from "next-intl";
import { Info, PartyPopper, TicketX, Truck } from "lucide-react";
import { Alert } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { couponView, freeDeliveryView, lineOffer, savingRows, whyThisPrice, type CouponLike, type ExplanationLike, type OfferLike, type SavingRow } from "./logic";

/**
 * The money lines between the list price and the goods total (list price, then each sale price, promotion, bundle or buy-X-get-Y).
 * Empty when nothing was saved, so a cart without offers keeps its old "Subtotal" row.
 */
export function useSavingRows(input: { explanation?: ExplanationLike; listSubtotal: number | string; subtotal: number | string; promotionDiscount: number | string }): (SavingRow & { text: string })[] {
  const t = useTranslations("storepromo.summary");
  const locale = useLocale();
  const { explanation, listSubtotal, subtotal, promotionDiscount } = input;
  return React.useMemo(
    () =>
      savingRows({ explanation, listSubtotal, subtotal, promotionDiscount }, locale, t("offers")).map((row) => ({ ...row, text: row.kind === "list" ? t("listPrice") : row.label })),
    [explanation, listSubtotal, subtotal, promotionDiscount, locale, t],
  );
}

/** Cart and checkout rows as plain markup (the cart summary is hand-made; the checkout feeds the same rows to its AmountList). */
export function SavingRowsList({ rows }: { rows: (SavingRow & { text: string })[] }) {
  const money = useMoney();
  return (
    <>
      {rows.map((row) => (
        <div key={row.key} className={cn("flex justify-between gap-3", row.kind === "reason" && "text-success-ink")} data-testid={`summary-${row.key}`}>
          <dt className={row.kind === "list" ? "text-muted" : undefined}>{row.text}</dt>
          <dd className={row.kind === "list" ? "text-strong" : undefined}>
            {row.kind === "reason" ? "-" : ""}
            {money(row.value)}
          </dd>
        </div>
      ))}
    </>
  );
}

/** The one-line "why this price" the API's explanation carries, with its notes (why a coupon was or was not used, free delivery). */
export function WhyThisPrice({ explanation, className }: { explanation: ExplanationLike; className?: string }) {
  const t = useTranslations("storepromo.summary");
  const locale = useLocale();
  const money = useMoney();
  const why = whyThisPrice(explanation, locale);
  if (!why) return null;
  return (
    <div className={cn("space-y-1 rounded-control bg-sunken p-3 text-sm", className)} data-testid="why-this-price">
      <p className="flex items-start gap-2 text-strong">
        <Info className="mt-0.5 size-4 shrink-0 text-muted" aria-hidden />
        <span>
          <span className="font-medium">{t("whyTitle")}: </span>
          {why.labels.length > 0 ? t("whyLine", { reasons: why.labels.join(", "), saving: money(why.saving) }) : t("whyNone")}
        </span>
      </p>
      {why.notes.map((note) => (
        <p key={note} className="ps-6 text-muted [overflow-wrap:anywhere]">
          {note}
        </p>
      ))}
    </div>
  );
}

/** A coupon that was kept on the cart but lost to better promotions (decision P4): say so, with the API's own reason. */
export function CouponLostNotice({ coupon, className }: { coupon: CouponLike; className?: string }) {
  const t = useTranslations("storepromo.coupon");
  const locale = useLocale();
  const view = couponView(coupon, locale);
  if (view.state !== "lost") return null;
  return (
    <Alert variant="warning" className={className} data-testid="coupon-lost">
      <span className="flex items-start gap-2">
        <TicketX className="mt-0.5 size-4 shrink-0" aria-hidden />
        <span>
          <span className="block font-medium">{t("lostTitle", { code: view.code })}</span>
          <span className="block">{view.reason ?? t("lostFallback")}</span>
        </span>
      </span>
    </Alert>
  );
}

/** Free delivery in the cart: already won, or what is still missing. Honest about offers that only hold in some areas. */
export function FreeDeliveryHint({ offers, className }: { offers: OfferLike[] | null | undefined; className?: string }) {
  const t = useTranslations("storepromo.freeDelivery");
  const locale = useLocale();
  const money = useMoney();
  const view = freeDeliveryView(offers, locale);
  if (!view) return null;
  if (view.state === "free") {
    return (
      <p className={cn("flex items-center gap-2 rounded-control bg-success-soft p-2.5 text-sm font-medium text-success-ink", className)} data-testid="free-delivery-offer">
        <PartyPopper className="size-5 shrink-0" aria-hidden /> {t("won", { label: view.label })}
      </p>
    );
  }
  return (
    <p className={cn("flex items-start gap-2 text-sm text-strong", className)} data-testid="free-delivery-more">
      <Truck className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden />
      <span>{view.zoneLimited ? t("moreZones", { amount: money(view.amount), label: view.label }) : t("more", { amount: money(view.amount), label: view.label })}</span>
    </p>
  );
}

/** Checkout delivery block: a running free-delivery promotion made the charge zero, say which one and how much it waived (decision P7). */
export function DeliveryWaiverNote({ delivery, className }: { delivery: { freeDeliveryWaiver?: number | string | null; promotionLabelEn?: string | null; promotionLabelBn?: string | null }; className?: string }) {
  const t = useTranslations("storepromo.freeDelivery");
  const locale = useLocale();
  const money = useMoney();
  const waived = toNumber(delivery.freeDeliveryWaiver);
  if (waived <= 0) return null;
  const label = (locale === "bn" ? delivery.promotionLabelBn || delivery.promotionLabelEn : delivery.promotionLabelEn || delivery.promotionLabelBn)?.trim();
  return (
    <p className={cn("flex items-start gap-2 text-sm font-medium text-success-ink", className)} data-testid="delivery-waiver">
      <PartyPopper className="mt-0.5 size-4 shrink-0" aria-hidden /> {label ? t("waivedWith", { label, amount: money(waived) }) : t("waived", { amount: money(waived) })}
    </p>
  );
}

/** Under a cart line's unit price: the struck-through list price and the label of the promotion behind it. */
export function LineOfferNote({ line, className }: { line: Parameters<typeof lineOffer>[0]; className?: string }) {
  const t = useTranslations("storepromo.summary");
  const locale = useLocale();
  const money = useMoney();
  const offer = lineOffer(line, locale);
  if (!offer) return null;
  return (
    <p className={cn("flex flex-wrap items-baseline gap-x-2 text-sm", className)} data-testid="line-offer">
      <span className="text-muted line-through">
        <span className="sr-only">{t("wasPrice")} </span>
        {money(offer.list)}
      </span>
      {offer.label ? <span className="font-medium text-success-ink">{offer.label}</span> : null}
      {toNumber(line.orderDiscount) > 0 ? <span className="text-success-ink">{t("lineShare", { amount: money(line.orderDiscount) })}</span> : null}
    </p>
  );
}
