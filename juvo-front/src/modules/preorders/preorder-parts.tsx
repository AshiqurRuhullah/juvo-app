"use client";

import * as React from "react";
import { useLocale, useTranslations } from "next-intl";
import { CalendarClock, Hourglass } from "lucide-react";
import { Alert, Badge } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { useMoney } from "@/modules/catalog/format";
import { formatShipDate, latestShipDate, orderStateNotice, preorderLabel, type PreorderFacts } from "./logic";

/** "Ships around 20 Dec 2026" or "Ships when it is back in stock", in the page language. Nothing for a normal line. */
export function usePreorderText() {
  const t = useTranslations("preorders");
  const locale = useLocale();
  return (facts: PreorderFacts | null | undefined): string | null => {
    const label = preorderLabel(facts);
    if (!label) return null;
    return label.kind === "dated" ? t("shipsAround", { date: formatShipDate(label.date, locale) }) : t("shipsWhenBack");
  };
}

/** The "Pre-order" chip. It always has text, so the meaning is never only colour. */
export function PreorderBadge({ facts, className }: { facts: PreorderFacts | null | undefined; className?: string }) {
  const t = useTranslations("preorders");
  if (!preorderLabel(facts)) return null;
  return (
    <Badge variant="info" className={cn("bg-card/95", className)} data-testid="preorder-badge">
      <CalendarClock className="size-3 shrink-0" aria-hidden /> {t("label")}
    </Badge>
  );
}

/** Chip and ship date on one line, for a cart line, an order line or a card. */
export function PreorderTag({ facts, waiting = false, className }: { facts: PreorderFacts | null | undefined; waiting?: boolean; className?: string }) {
  const t = useTranslations("preorders");
  const text = usePreorderText();
  const line = text(facts);
  if (!line) return null;
  return (
    <p className={cn("flex flex-wrap items-center gap-x-2 gap-y-1 text-sm", className)} data-testid="preorder-tag">
      <Badge variant="info">
        <CalendarClock className="size-3 shrink-0" aria-hidden /> {t("label")}
      </Badge>
      <span className="text-muted">{line}</span>
      {waiting ? (
        <Badge variant="warning">
          <Hourglass className="size-3 shrink-0" aria-hidden /> {t("waitingForStock")}
        </Badge>
      ) : null}
    </p>
  );
}

/** Product page notice: what a pre-order means, with the date and the advance when one is due. */
export function PreorderProductNotice({ facts, className }: { facts: PreorderFacts | null | undefined; className?: string }) {
  const t = useTranslations("preorders.product");
  const text = usePreorderText();
  const line = text(facts);
  if (!line) return null;
  return (
    <Alert variant="info" className={className} title={`${t("title")}: ${line}`} data-testid="preorder-notice">
      {t("body")}
    </Alert>
  );
}

/** Cart and checkout notice for a basket that holds at least one pre-order line (decision P14: the whole order waits). */
export function PreorderCartNotice({ lines, advance, className }: { lines: PreorderFacts[]; advance?: number | string | null; className?: string }) {
  const t = useTranslations("preorders.cart");
  const locale = useLocale();
  const money = useMoney();
  const advanceNumber = Number(advance ?? 0);
  if (!lines.some((l) => l.isPreorder)) return null;
  const latest = latestShipDate(lines);
  return (
    <Alert variant="info" className={className} title={t("title")} data-testid="preorder-cart-notice">
      <span className="block">{latest ? t("bodyDated", { date: formatShipDate(latest, locale) }) : t("body")}</span>
      {advanceNumber > 0 ? <span className="mt-1 block font-medium" data-testid="preorder-advance">{t("advance", { amount: money(advanceNumber) })}</span> : null}
    </Alert>
  );
}

/** Customer order pages: why the order is "Waiting", or that the stock arrived. */
export function OrderPreorderNotice({ state, className }: { state: string | null | undefined; className?: string }) {
  const t = useTranslations("preorders.order");
  const notice = orderStateNotice(state);
  if (!notice) return null;
  return notice === "waiting" ? (
    <Alert variant="warning" className={className} title={t("waitingTitle")} data-testid="preorder-waiting">
      {t("waitingBody")}
    </Alert>
  ) : (
    <Alert variant="success" className={className} title={t("readyTitle")} data-testid="preorder-ready">
      {t("readyBody")}
    </Alert>
  );
}

/** Thank-you page: the order holds pre-order items, so it is placed but waits; nothing is reserved until the stock arrives. */
export function PreorderConfirmed({ className }: { className?: string }) {
  const t = useTranslations("preorders.confirmed");
  return (
    <Alert variant="info" className={className} title={t("title")} data-testid="preorder-confirmed">
      {t("body")}
    </Alert>
  );
}
