"use client";

import { useTranslations } from "next-intl";
import { CircleAlert } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { shipmentTone } from "@/modules/couriers/status";

/** A parcel status as a badge. A failed delivery always carries an icon next to the colour and the text. */
export function ParcelStatusBadge({ status }: { status: string }) {
  const t = useTranslations("booking.status");
  const tone = shipmentTone(status);
  return (
    <Badge variant={tone === "neutral" ? "neutral" : tone} data-testid="parcel-status" data-status={status}>
      {status === "DeliveryFailed" ? <CircleAlert className="size-3.5" aria-hidden /> : null}
      {t.has(status) ? t(status) : status}
    </Badge>
  );
}
