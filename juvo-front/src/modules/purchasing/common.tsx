"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { AlertTriangle, CheckCircle2 } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { toNumber } from "@/modules/catalog/format";
import { PAYMENT_TONE, PO_TONE } from "./logic";

/** Status of a purchase order. The words carry the meaning; colour only supports them. */
export const PoStatusBadge = React.memo(function PoStatusBadge({ status }: { status: string }) {
  const t = useTranslations("purchasing.status");
  return (
    <Badge variant={PO_TONE[status] ?? "neutral"} data-testid="po-status">
      {t.has(status) ? t(status) : status}
    </Badge>
  );
});

/** Payment to the supplier: unpaid, partly paid, paid; "overdue" is added with an icon when the invoice is past due. */
export const PayStatusBadge = React.memo(function PayStatusBadge({ status, overdue }: { status: string; overdue?: boolean }) {
  const t = useTranslations("purchasing.payStatus");
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5">
      <Badge variant={PAYMENT_TONE[status] ?? "neutral"} data-testid="pay-status">
        {status === "Paid" ? <CheckCircle2 className="size-3.5" aria-hidden /> : null}
        {t.has(status) ? t(status) : status}
      </Badge>
      {overdue ? (
        <Badge variant="danger" data-testid="po-overdue">
          <AlertTriangle className="size-3.5" aria-hidden /> {t("overdue")}
        </Badge>
      ) : null}
    </span>
  );
});

/** Label and value pair used in the summary strips. */
export function Stat({ label, value, testId, emphasis }: { label: string; value: React.ReactNode; testId?: string; emphasis?: boolean }) {
  return (
    <div className="min-w-0">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className={emphasis ? "text-xl font-semibold tabular-nums text-strong" : "font-medium tabular-nums text-strong"} data-testid={testId}>
        {value}
      </dd>
    </div>
  );
}

/** Money with always two decimals (purchasing and cash screens show exact figures), in BDT in the current language. */
export function useMoney2() {
  const format = useFormatter();
  return React.useCallback(
    (value: number | string | null | undefined) =>
      format.number(toNumber(value), { style: "currency", currency: "BDT", currencyDisplay: "narrowSymbol", minimumFractionDigits: 2, maximumFractionDigits: 2 }),
    [format],
  );
}

/** A date such as "2026-11-15" in the current language (no time zone shifting: it is a calendar date). */
export function useDateOnly() {
  const format = useFormatter();
  return React.useCallback(
    (value: string | null | undefined) => {
      if (!value) return "-";
      const [y, m, d] = value.slice(0, 10).split("-").map(Number);
      return format.dateTime(new Date(Date.UTC(y!, m! - 1, d!)), { dateStyle: "medium", timeZone: "UTC" });
    },
    [format],
  );
}
