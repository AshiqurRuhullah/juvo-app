"use client";

import * as React from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { CheckCircle2, Circle, CircleX } from "lucide-react";
import { Alert, Badge } from "@/components/ui/surfaces";
import { mediaUrl } from "@/lib/media";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { isDangerStatus, refundRows, statusTone, type Breakdown, type ReturnStatus } from "./logic";

/** Status badge. The words carry the meaning; the two refusals also carry an icon so colour is never the only signal. */
export function ReturnStatusBadge({ status, className }: { status: ReturnStatus; className?: string }) {
  const t = useTranslations("returns.status");
  return (
    <Badge variant={statusTone(status)} className={className} data-testid="return-status">
      {isDangerStatus(status) ? <CircleX className="size-3.5" aria-hidden /> : null}
      {t(status)}
    </Badge>
  );
}

/** Reason text in the current language for a reason code (the API lists the same seven codes). */
export function useReasonLabel() {
  const t = useTranslations("returns.reasons");
  return (code: string) => (t.has(code) ? t(code) : code);
}

/** A date that has no time of its own (a pickup day). */
export function useDay() {
  const format = useFormatter();
  return (value: string | null | undefined) => (value ? format.dateTime(new Date(`${value.slice(0, 10)}T00:00:00`), { dateStyle: "medium" }) : "-");
}

/** Small square product picture (the API's /files address is shown through /media). */
export function Thumb({ src, alt = "", size = 56, className }: { src: string | null | undefined; alt?: string; size?: number; className?: string }) {
  const url = mediaUrl(src);
  return (
    <span className={cn("skeleton relative shrink-0 overflow-hidden rounded-control border border-line bg-sunken", className)} style={{ width: size, height: size }}>
      {url ? (
        // eslint-disable-next-line @next/next/no-img-element -- small cached thumbnails served from /media; nothing for next/image to resize
        <img src={url} alt={alt} width={size} height={size} loading="lazy" className="size-full object-cover" />
      ) : null}
    </span>
  );
}

/** "SKU - colour - size" under a product name. */
export function VariantLine({ sku, colorName, size, className }: { sku: string; colorName?: string | null; size?: string | null; className?: string }) {
  const parts = [colorName, size].filter(Boolean).join(" · ");
  return (
    <span className={cn("block text-sm text-muted [overflow-wrap:anywhere]", className)}>
      <span className="font-mono">{sku}</span>
      {parts ? ` · ${parts}` : ""}
    </span>
  );
}

/**
 * The refund in plain rows: items, delivery charge when it comes back, a return charge taken off, and the total. An estimate says so
 * until the parcel is inspected.
 */
export function RefundBox({ refund, className }: { refund: Breakdown; className?: string }) {
  const t = useTranslations("returns.refund");
  const money = useMoney();
  const { rows, total, final } = refundRows(refund);
  return (
    <div className={cn("space-y-3", className)} data-testid="refund-box">
      <dl className="space-y-2">
        {rows.map((row) => (
          <div key={row.key} className="flex items-baseline justify-between gap-3">
            <dt className="text-muted">{t(row.key)}</dt>
            <dd className="font-medium tabular-nums text-strong">
              {row.sign === "-" ? "- " : ""}
              {money(row.amount)}
            </dd>
          </div>
        ))}
        <div className="flex items-baseline justify-between gap-3 border-t border-line pt-2">
          <dt className="font-semibold text-strong">{final ? t("totalFinal") : t("totalEstimate")}</dt>
          <dd className="text-xl font-bold tabular-nums text-strong" data-testid="refund-total">
            {money(total)}
          </dd>
        </div>
      </dl>
      {!final ? <Alert variant="info">{t("estimateNote")}</Alert> : null}
    </div>
  );
}

export type TimelineEntry = { from: string | null; to: string; reason: string | null; actorName: string | null; at: string };

/** Immutable history, oldest first. Staff see who did each step; the customer's version has no names. */
export function ReturnTimeline({ entries, dateTime }: { entries: TimelineEntry[]; dateTime: (value: string) => string }) {
  const t = useTranslations("returns.status");
  const tt = useTranslations("returns.timeline");
  const reasonLabel = useReasonLabel();
  return (
    <ol className="space-y-4" data-testid="return-timeline" aria-label={tt("label")}>
      {entries.map((entry, index) => {
        const status = entry.to as ReturnStatus;
        const last = index === entries.length - 1;
        const danger = isDangerStatus(status);
        return (
          <li key={`${entry.to}-${entry.at}-${index}`} className="flex items-start gap-3">
            {danger ? <CircleX className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden /> : last ? <Circle className="mt-0.5 size-5 shrink-0 fill-primary text-primary" aria-hidden /> : <CheckCircle2 className="mt-0.5 size-5 shrink-0 text-success" aria-hidden />}
            <div className="min-w-0">
              <p className="font-semibold text-strong">{t.has(entry.to) ? t(entry.to) : entry.to}</p>
              <p className="text-sm text-muted">
                {dateTime(entry.at)}
                {entry.actorName ? ` · ${entry.actorName}` : ""}
              </p>
              {entry.reason ? <p className="mt-0.5 text-sm text-strong [overflow-wrap:anywhere]">{reasonLabel(entry.reason)}</p> : null}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

/** Product name in the language of the page (the order line keeps the English name; Bengali only when the API sends it). */
export function useLocalName() {
  const locale = useLocale();
  return (en: string, bn?: string | null) => (locale === "bn" && bn ? bn : en);
}

export { toNumber };
