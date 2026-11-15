"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Check, CircleAlert, CircleDot, ExternalLink, MapPin, Truck } from "lucide-react";
import { Alert, Badge } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import type { Schemas } from "@/lib/api/types";
import { safeTrackingUrl, shipmentTone } from "@/modules/couriers/status";
import { Section } from "../common";

export type CustomerShipment = NonNullable<Schemas["TrackingDto"]["shipment"]>;

/**
 * "Courier and tracking" for the customer (public tracking page and the signed-in order page): the courier, the consignment number,
 * a link to the courier's own page (https only) and the courier's steps as a timeline. Shown only once a parcel is booked. The API
 * already removes staff names, internal notes and raw courier statuses; this card shows nothing beyond what it is given.
 */
export function CourierTrackingCard({ shipment, id }: { shipment: CustomerShipment | null | undefined; id: string }) {
  const t = useTranslations("booking.customer");
  const ts = useTranslations("booking.status");
  const dateTime = useDateTime();
  if (!shipment) return null;

  const link = safeTrackingUrl(shipment.trackingUrl);
  const attempts = Number(shipment.attempts);
  const failed = shipment.status === "DeliveryFailed";
  const tone = shipmentTone(shipment.status);
  const events = shipment.events;
  const last = events.length - 1;

  return (
    <Section title={t("title")} id={id} className="space-y-4">
      <div data-testid="courier-card" data-status={shipment.status}>
        <div className="flex flex-wrap items-center gap-2">
          <Truck className="size-5 text-primary" aria-hidden />
          <span className="font-semibold text-strong" data-testid="courier-name">
            {shipment.courierName}
          </span>
          <Badge variant={tone} data-testid="courier-status">
            {failed ? <CircleAlert className="size-3.5" aria-hidden /> : null}
            {ts.has(shipment.status) ? ts(shipment.status) : shipment.status}
          </Badge>
        </div>
        <dl className="mt-3 space-y-1.5 text-sm">
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t("consignment")}</dt>
            <dd className="text-end font-mono font-semibold text-strong [overflow-wrap:anywhere]" data-testid="courier-consignment">
              {shipment.consignmentId}
            </dd>
          </div>
          {attempts > 0 ? (
            <div className="flex justify-between gap-3">
              <dt className="text-muted">{t("attempts")}</dt>
              <dd className="text-end font-medium text-strong" data-testid="courier-attempts">
                {attempts}
              </dd>
            </div>
          ) : null}
        </dl>
        {link ? (
          <a href={link} target="_blank" rel="noopener noreferrer" className="mt-2 inline-flex min-h-11 items-center gap-1.5 font-semibold text-strong underline underline-offset-4" data-testid="courier-link">
            <ExternalLink className="size-4" aria-hidden /> {t("trackWithCourier")}
          </a>
        ) : null}
      </div>

      {failed ? (
        <Alert variant="danger" title={t("failedTitle")} data-testid="courier-failed">
          {t("failedText", { count: attempts })}
        </Alert>
      ) : null}

      {events.length > 0 ? (
        <ol className="relative space-y-4 border-s border-line ps-6" aria-label={t("timeline")} data-testid="courier-timeline">
          {events.map((event, index) => {
            const current = index === last;
            const bad = event.status === "DeliveryFailed";
            return (
              <li key={`${event.occurredAt}-${index}`} className="relative" data-testid="courier-event" aria-current={current ? "step" : undefined}>
                <span
                  className={cn(
                    "absolute -start-[2.1rem] top-0.5 flex size-6 items-center justify-center rounded-full border-2 bg-card",
                    bad ? "border-danger text-danger" : current ? "border-primary text-primary" : "border-success text-success",
                  )}
                  aria-hidden
                >
                  {bad ? <CircleAlert className="size-3.5" /> : current ? <CircleDot className="size-3.5" /> : <Check className="size-3.5" />}
                </span>
                <p className={cn("text-sm", current ? "font-semibold text-strong" : "text-strong")}>{event.description ?? (ts.has(event.status) ? ts(event.status) : event.status)}</p>
                <p className="flex flex-wrap items-center gap-x-3 text-sm text-muted">
                  {event.location ? (
                    <span className="inline-flex items-center gap-1">
                      <MapPin className="size-3.5" aria-hidden /> {event.location}
                    </span>
                  ) : null}
                  <time dateTime={event.occurredAt}>{dateTime(event.occurredAt)}</time>
                </p>
              </li>
            );
          })}
        </ol>
      ) : (
        <p className="text-sm text-muted">{t("noEvents")}</p>
      )}
    </Section>
  );
}
