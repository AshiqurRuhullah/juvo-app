"use client";

import * as React from "react";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere, canInBranch } from "@/lib/auth/permissions";

/**
 * What this staff member may do with suppliers, purchase orders and receipts. For one order or receipt pass its branch id (the API checks
 * every action in the order's own branch); for lists leave it out (held in any branch). Display only: the API still refuses what is not allowed.
 */
export function usePurchasingPermissions(branchId?: string) {
  const { me } = useSession();
  return React.useMemo(() => {
    const has = (code: string) => (branchId ? canInBranch(me, code, branchId) : canAnywhere(me, code));
    return {
      supplierView: has("purchasing.supplier.view"),
      supplierCreate: has("purchasing.supplier.create"),
      supplierEdit: has("purchasing.supplier.edit"),
      poView: has("purchasing.po.view"),
      poCreate: has("purchasing.po.create"),
      poEdit: has("purchasing.po.edit"),
      poSend: has("purchasing.po.send"),
      poReceive: has("purchasing.po.receive"),
      poCancel: has("purchasing.po.cancel"),
      receiptView: has("purchasing.receipt.view"),
      payablePay: has("purchasing.payable.pay"),
      cashbookView: has("accounting.cashbook.view"),
      /** Applying the cost of a receipt needs the cost permission (the API refuses everyone else). */
      viewCost: canAnywhere(me, "catalog.product.view-cost"),
    };
  }, [me, branchId]);
}
