"use client";

import { useFormatter, useTranslations } from "next-intl";
import { CalendarClock, CircleAlert } from "lucide-react";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useOrderErrorText } from "../api";
import { AmountList } from "../common";
import type { ManualQuote } from "./use-desk-quote";

type QuoteState = { data?: ManualQuote; error: unknown; isError: boolean; isLoading: boolean; ready: boolean; settling: boolean };

/**
 * The order desk's price preview, straight from the server's own calculation (nothing is estimated here): the items with the branch's
 * stock, coupon, discount, the delivery charge (the shop charge and, when overridden, the new one), the total, the advance and the
 * delivery window. The API's refusals (not enough stock, bad coupon, discount too large, no cash on delivery) show inline.
 */
export function DeskSummary({ quote }: { quote: QuoteState }) {
  const t = useTranslations("booking.desk");
  const money = useMoney();
  const format = useFormatter();
  const errorText = useOrderErrorText();
  const data = quote.data;
  const day = (value: string | null | undefined) => (value ? format.dateTime(new Date(value), { dateStyle: "medium" }) : null);

  if (!quote.ready) return <p className="text-sm text-muted" data-testid="desk-quote-hint">{t("needMore")}</p>;
  if (quote.isError) {
    return (
      <Alert variant="danger" data-testid="desk-quote-error" aria-live="polite">
        {errorText(quote.error)}
      </Alert>
    );
  }
  if (!data) return <Skeleton className="h-40" aria-busy data-testid="desk-quote-loading" />;

  const overridden = toNumber(data.deliveryCharge) !== toNumber(data.standardDeliveryCharge);
  const advance = toNumber(data.advanceRequired);

  return (
    <div className="space-y-4" aria-busy={quote.settling} data-testid="desk-quote" data-settling={quote.settling ? "1" : "0"}>
      <ul className="space-y-2 text-sm" aria-label={t("lines")} data-testid="quote-lines">
        {data.lines.map((l) => {
          const short = toNumber(l.availableInBranch) < toNumber(l.quantity);
          return (
            <li key={l.variantId} className="flex flex-wrap items-start justify-between gap-x-3" data-sku={l.sku}>
              <span className="min-w-0">
                <span className="block font-medium text-strong [overflow-wrap:anywhere]">
                  {l.name} × {toNumber(l.quantity)}
                </span>
                <span className="block text-muted">
                  <span className="font-mono">{l.sku}</span> · {t("inBranch", { count: toNumber(l.availableInBranch) })}
                </span>
                {short ? (
                  <span className="flex items-center gap-1 font-medium text-danger-ink">
                    <CircleAlert className="size-4" aria-hidden /> {t("short")}
                  </span>
                ) : null}
              </span>
              <span className="font-semibold tabular-nums text-strong">{money(l.lineTotal)}</span>
            </li>
          );
        })}
      </ul>
      <AmountList
        className="border-t border-line pt-4"
        rows={[
          { key: "subtotal", label: t("subtotal"), value: data.subtotal, always: true },
          { key: "coupon", label: t("couponRow", { code: data.couponCode ?? "" }), value: data.couponDiscount, negative: true },
          { key: "discount", label: t("discount"), value: data.manualDiscount, negative: true },
          { key: "delivery", label: t("delivery"), value: data.deliveryCharge, always: true },
          { key: "total", label: t("total"), value: data.grandTotal, strong: true },
          { key: "advance", label: t("advance"), value: data.advanceRequired },
        ]}
      />
      {overridden ? (
        <p className="text-sm text-muted" data-testid="quote-override-note">
          {t("overridden", { standard: money(data.standardDeliveryCharge) })}
        </p>
      ) : null}
      {advance > 0 ? <Alert variant="warning">{t("advanceNote", { amount: money(advance) })}</Alert> : null}
      {data.earliestDelivery ? (
        <p className="flex items-start gap-2 text-sm text-muted">
          <CalendarClock className="mt-0.5 size-4 shrink-0" aria-hidden />
          <span data-testid="quote-window">{t("window", { from: day(data.earliestDelivery) ?? "", to: day(data.latestDelivery ?? data.earliestDelivery) ?? "" })}</span>
        </p>
      ) : null}
      <p className="text-sm text-muted">{t("exact")}</p>
    </div>
  );
}
