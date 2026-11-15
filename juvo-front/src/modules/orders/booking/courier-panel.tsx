"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ExternalLink, Printer, Truck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { useCourierPermissions } from "@/modules/couriers/permissions";
import { useOrderShipments, type ShipmentListItem } from "@/modules/couriers/api";
import { formatMoney, isOpenShipment, safeTrackingUrl } from "@/modules/couriers/status";
import { Section } from "../common";
import { ParcelStatusBadge } from "./shipment-badge";

// The dialog (and its suggestions query) is only downloaded when someone opens it.
const BookCourierDialog = dynamic(() => import("./book-dialog"), { ssr: false });

type Order = { id: string; number: string; branchId: string; status: string };

function ParcelRow({ parcel, canView, canPrint }: { parcel: ShipmentListItem; canView: boolean; canPrint: boolean }) {
  const t = useTranslations("booking.panel");
  const dateTime = useDateTime();
  const link = safeTrackingUrl(parcel.trackingUrl);
  const cod = Number(parcel.codAmount);
  return (
    <li className="space-y-2 rounded-control border border-line p-3" data-testid="parcel" data-status={parcel.status} data-kind={parcel.kind}>
      <div className="flex flex-wrap items-center gap-2">
        <Badge variant="outline">{t(`kind.${parcel.kind}`)}</Badge>
        <ParcelStatusBadge status={parcel.status} />
        <span className="text-sm text-muted">{dateTime(parcel.bookedAt)}</span>
      </div>
      <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-2">
        <div className="flex justify-between gap-3">
          <dt className="text-muted">{t("courier")}</dt>
          <dd className="text-end font-medium text-strong">{parcel.courierName}</dd>
        </div>
        <div className="flex justify-between gap-3">
          <dt className="text-muted">{t("consignment")}</dt>
          <dd className="text-end font-mono font-medium text-strong [overflow-wrap:anywhere]" data-testid="parcel-consignment">
            {parcel.consignmentId}
          </dd>
        </div>
        {cod > 0 ? (
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t("cod")}</dt>
            <dd className="text-end font-medium text-strong">{formatMoney(cod)}</dd>
          </div>
        ) : null}
        {parcel.status === "DeliveryFailed" && parcel.failureReason ? (
          <div className="flex justify-between gap-3 sm:col-span-2">
            <dt className="text-muted">{t("failure")}</dt>
            <dd className="text-end text-danger-ink [overflow-wrap:anywhere]">{parcel.failureReason}</dd>
          </div>
        ) : null}
      </dl>
      <div className="flex flex-wrap gap-2">
        {link ? (
          <a href={link} target="_blank" rel="noopener noreferrer" className="inline-flex min-h-11 items-center gap-1.5 font-semibold text-strong underline underline-offset-4" data-testid="parcel-tracking">
            <ExternalLink className="size-4" aria-hidden /> {t("tracking")}
          </a>
        ) : null}
        {canView ? (
          <Button asChild variant="secondary">
            <Link href={`/admin/shipments/${parcel.id}`} data-testid="parcel-open">
              {t("open")}
            </Link>
          </Button>
        ) : null}
        {canPrint && parcel.status !== "Cancelled" ? (
          <Button asChild variant="secondary">
            <Link href={`/admin/shipments/${parcel.id}/label`} data-testid="parcel-label">
              <Printer aria-hidden /> {t("label")}
            </Link>
          </Button>
        ) : null}
      </div>
    </li>
  );
}

/**
 * "Delivery and courier" on the staff order page: this order's parcels (forward and return) and, for a packed order with no open
 * parcel, the Book courier button. Loaded lazily (the order page does not wait for it). Buttons are drawn only when the person may
 * use them in the order's own branch and the order is in a state the API would accept; the API checks again.
 */
export default function CourierPanel({ order }: { order: Order }) {
  const t = useTranslations("booking.panel");
  const perms = useCourierPermissions(order.branchId);
  const parcels = useOrderShipments(order.id, perms.shipmentView);
  const [open, setOpen] = React.useState(false);
  if (!perms.shipmentView && !perms.book) return null;

  const list = parcels.data?.items ?? [];
  const hasOpenForward = list.some((p) => p.kind === "Forward" && isOpenShipment(p.status));
  const packed = order.status === "Packed";
  const canBook = perms.book && packed && !hasOpenForward && (parcels.isSuccess || !perms.shipmentView);

  return (
    <Section title={t("title")} id="od-courier" className="space-y-4">
      {parcels.isLoading && perms.shipmentView ? <Skeleton className="h-20" aria-busy /> : null}
      {parcels.isError ? <p className="text-sm text-danger">{t("loadFailed")}</p> : null}
      {parcels.isSuccess && list.length === 0 ? <p className="text-sm text-muted">{packed ? t("readyToBook") : t("noneYet")}</p> : null}
      {list.length > 0 ? (
        <ul className="space-y-3" data-testid="order-parcels">
          {list.map((p) => (
            <ParcelRow key={p.id} parcel={p} canView={perms.shipmentView} canPrint={perms.printLabel} />
          ))}
        </ul>
      ) : null}

      {canBook ? (
        <Button onClick={() => setOpen(true)} data-testid="book-courier">
          <Truck aria-hidden /> {t("book")}
        </Button>
      ) : null}
      {perms.book && !packed && !hasOpenForward && ["Placed", "Confirmed"].includes(order.status) ? (
        <p className="text-sm text-muted" data-testid="book-not-ready">
          {t("packFirst")}
        </p>
      ) : null}
      {open ? <BookCourierDialog orderId={order.id} orderNumber={order.number} branchId={order.branchId} open onOpenChange={setOpen} /> : null}
    </Section>
  );
}
