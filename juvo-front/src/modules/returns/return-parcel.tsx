"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { ExternalLink } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { useShipments } from "@/modules/couriers/api";
import { useCourierPermissions } from "@/modules/couriers/permissions";
import { safeTrackingUrl } from "@/modules/couriers/status";
import { ParcelStatusBadge } from "@/modules/orders/booking/shipment-badge";

/**
 * The reverse parcel of a return (`GET /shipments?orderId=..&kind=Return`): courier, consignment, status and a link to the parcel. Shown
 * only to people who may view parcels in the return's branch, and only when a parcel exists (a return picked up by hand has none).
 * The parcel list item carries no return id, so an order with several returns shows each of its reverse parcels.
 */
export default function ReturnParcel({ orderId, branchId }: { orderId: string; branchId: string }) {
  const t = useTranslations("booking.returns");
  const dateTime = useDateTime();
  const perms = useCourierPermissions(branchId);
  const parcels = useShipments({ page: 1, pageSize: 5, orderId, kind: "Return", sort: "bookedAt" }, { enabled: perms.shipmentView });
  if (!perms.shipmentView) return null;
  if (parcels.isLoading) return <Skeleton className="h-24" aria-busy />;
  const list = parcels.data?.items ?? [];
  if (list.length === 0) return null;

  return (
    <Card data-testid="return-parcels">
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent>
        <ul className="space-y-3">
          {list.map((p) => {
            const link = safeTrackingUrl(p.trackingUrl);
            return (
              <li key={p.id} className="space-y-2 rounded-control border border-line p-3" data-testid="return-parcel" data-status={p.status}>
                <div className="flex flex-wrap items-center gap-2">
                  <ParcelStatusBadge status={p.status} />
                  <span className="text-sm text-muted">{dateTime(p.bookedAt)}</span>
                </div>
                <p className="text-sm">
                  <span className="text-muted">{t("courier")}: </span>
                  <span className="font-medium text-strong">{p.courierName}</span> · <span className="font-mono text-strong" data-testid="return-consignment">{p.consignmentId}</span>
                </p>
                <div className="flex flex-wrap gap-2">
                  {link ? (
                    <a href={link} target="_blank" rel="noopener noreferrer" className="inline-flex min-h-11 items-center gap-1.5 font-semibold text-strong underline underline-offset-4">
                      <ExternalLink className="size-4" aria-hidden /> {t("tracking")}
                    </a>
                  ) : null}
                  <Button asChild variant="secondary">
                    <Link href={`/admin/shipments/${p.id}`} data-testid="return-parcel-open">
                      {t("open")}
                    </Link>
                  </Button>
                </div>
              </li>
            );
          })}
        </ul>
      </CardContent>
    </Card>
  );
}
