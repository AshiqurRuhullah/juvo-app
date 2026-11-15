"use client";

import * as React from "react";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere, canInBranch } from "@/lib/auth/permissions";

/**
 * What this staff member may do with couriers, parcels and COD settlements. For one parcel or order pass its branch id (the API
 * checks every action in the parcel's own branch); for lists and settings leave it out (held in any branch). Display only: the API
 * still refuses what is not allowed.
 */
export function useCourierPermissions(branchId?: string) {
  const { me } = useSession();
  return React.useMemo(() => {
    const has = (code: string) => (branchId ? canInBranch(me, code, branchId) : canAnywhere(me, code));
    return {
      courierView: has("couriers.courier.view"),
      courierEdit: has("couriers.courier.edit"),
      shipmentView: has("couriers.shipment.view"),
      book: has("couriers.shipment.book"),
      bulkBook: has("couriers.shipment.bulk-book"),
      cancel: has("couriers.shipment.cancel"),
      printLabel: has("couriers.shipment.print-label"),
      simulate: has("couriers.shipment.simulate"),
      codView: has("couriers.cod.view"),
      codSettle: has("couriers.cod.settle"),
    };
  }, [me, branchId]);
}
