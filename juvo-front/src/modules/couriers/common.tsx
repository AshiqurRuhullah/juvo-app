"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Check, CircleX, ExternalLink } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { JOURNEY, journeyIndex, safeTrackingUrl, shipmentTone } from "./status";

/** Status badge of a parcel. The words carry the meaning; a failed delivery also carries an icon so colour is never alone. */
export const ShipmentStatusBadge = React.memo(function ShipmentStatusBadge({ status, className }: { status: string; className?: string }) {
  const t = useTranslations("shipments.status");
  return (
    <Badge variant={shipmentTone(status)} className={className} data-testid="shipment-status">
      {status === "DeliveryFailed" ? <CircleX className="size-3.5" aria-hidden /> : null}
      {t.has(status) ? t(status) : status}
    </Badge>
  );
});

/** Link to the courier's tracking page, only for https addresses. Opens in a new tab without leaking the referrer. */
export function TrackingLink({ url, className }: { url: string | null | undefined; className?: string }) {
  const t = useTranslations("shipments.detail");
  const safe = safeTrackingUrl(url);
  if (!safe) return null;
  return (
    <a href={safe} target="_blank" rel="noopener noreferrer" className={cn("inline-flex min-h-11 items-center gap-1.5 rounded-control font-medium text-primary hover:underline", className)} data-testid="tracking-link">
      {t("trackingLink")} <ExternalLink className="size-4" aria-hidden />
      <span className="sr-only">{t("opensInNewTab")}</span>
    </a>
  );
}

/**
 * The normal journey as five steps with the current one marked. A failed delivery sits at "out for delivery" with an icon and its own
 * text; a cancelled or returned parcel is off the path and says so instead of pretending to be on it.
 */
export function JourneyBar({ status }: { status: string }) {
  const t = useTranslations("shipments.status");
  const tj = useTranslations("shipments.journey");
  const index = journeyIndex(status);
  const failed = status === "DeliveryFailed";
  const off = index < 0; // Cancelled or Returned
  return (
    <div data-testid="journey" data-status={status}>
      <ol className="grid grid-cols-5 gap-1" aria-label={tj("label")}>
        {JOURNEY.map((step, i) => {
          const done = !off && i < index;
          const current = !off && i === index;
          const isFail = current && failed;
          return (
            <li key={step} className="min-w-0" aria-current={current ? "step" : undefined} data-state={isFail ? "failed" : current ? "current" : done ? "done" : "todo"}>
              <span className={cn("block h-1.5 rounded-full", isFail ? "bg-danger" : done || current ? "bg-primary" : "bg-line")} aria-hidden />
              <span className="mt-2 flex items-start gap-1 text-xs leading-4 sm:text-sm sm:leading-5">
                {isFail ? <CircleX className="mt-0.5 size-4 shrink-0 text-danger" aria-hidden /> : done ? <Check className="mt-0.5 size-4 shrink-0 text-primary" aria-hidden /> : null}
                <span className={cn("min-w-0 [overflow-wrap:anywhere]", current ? "font-semibold text-strong" : done ? "text-strong" : "text-muted")}>
                  {isFail ? t("DeliveryFailed") : t(step)}
                </span>
              </span>
            </li>
          );
        })}
      </ol>
      {off ? (
        <p className="mt-3 flex items-center gap-2 text-sm font-medium text-strong" data-testid="journey-off">
          <CircleX className="size-4 text-muted" aria-hidden /> {tj(status === "Cancelled" ? "cancelled" : "returned")}
        </p>
      ) : null}
    </div>
  );
}
