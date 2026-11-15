"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { toast } from "@/components/ui/toaster";
import { useSession } from "@/components/providers/session-provider";
import { useLiveRefresh, type LiveEvent, type LiveStatus } from "@/lib/realtime/use-live";
import { useMoney } from "@/modules/catalog/format";
import { DETAIL_EVENTS, LIST_EVENTS, liveBranch, newOrderToast } from "./logic";

/**
 * The order list follows the active branch's live group (or every branch when the session is allowed to see them all) and refreshes
 * itself when an order is placed, changes status, is paid or gets a courier scan. A new order also shows a toast with a link.
 */
export function useOrdersListLive(): LiveStatus {
  const { me } = useSession();
  const t = useTranslations("booking.live");
  const money = useMoney();
  const branch = liveBranch(me);
  const onEvent = React.useCallback(
    (event: LiveEvent) => {
      const info = newOrderToast(event);
      if (!info) return;
      toast.success(t("newOrder", { number: info.number, total: info.total === null ? "-" : money(info.total) }), {
        description: info.orderId ? (
          <Link href={`/admin/orders/${info.orderId}`} className="inline-flex min-h-11 items-center font-semibold underline underline-offset-4" data-testid="new-order-link">
            {t("open")}
          </Link>
        ) : undefined,
      });
    },
    [t, money],
  );
  return useLiveRefresh({ mode: "staff", branch, queryKeys: [["orders", "list"], ["orders", "sources"]], events: LIST_EVENTS, onEvent, enabled: !!branch });
}

/** One staff order page joins its own order group: every event it receives is about this order. */
export function useOrderDetailLive(orderId: string): LiveStatus {
  return useLiveRefresh({
    mode: "staff",
    orderIds: [orderId],
    queryKeys: [["orders", "detail", orderId], ["couriers", "shipments"], ["payments"]],
    events: DETAIL_EVENTS,
  });
}

/**
 * The return page follows the return's branch group (reverse parcel scans are published to branch groups only, not to the order
 * group) and refreshes this return and its parcel when anything of that kind arrives.
 */
export function useReturnLive(branchId: string | undefined, returnId: string): LiveStatus {
  return useLiveRefresh({
    mode: "staff",
    branch: branchId,
    queryKeys: [["returns", "detail", returnId], ["couriers", "shipments"]],
    events: ["ReturnStatusChanged", "ShipmentEventAdded"],
    enabled: !!branchId,
  });
}
