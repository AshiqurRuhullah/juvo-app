"use client";

import * as React from "react";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere, canInBranch } from "@/lib/auth/permissions";

/**
 * What this staff member may do with orders. For one order pass its branch id (every action is checked in the order's own branch,
 * like the API does); for lists and the order desk leave it out (held in any branch). The API still refuses what is not allowed.
 */
export function useOrderPermissions(branchId?: string) {
  const { me } = useSession();
  return React.useMemo(() => {
    const has = (code: string) => (branchId ? canInBranch(me, code, branchId) : canAnywhere(me, code));
    return {
      view: has("orders.order.view"),
      confirm: has("orders.order.confirm"),
      pack: has("orders.order.pack"),
      cancel: has("orders.order.cancel"),
      edit: has("orders.order.edit"),
      note: has("orders.order.note"),
      reassign: has("orders.order.reassign-branch"),
      print: has("orders.order.print"),
      bulk: has("orders.order.bulk-action"),
      create: has("orders.order.create"),
      applyDiscount: has("orders.order.apply-discount"),
      overrideDelivery: has("orders.order.override-delivery"),
      sources: has("orders.source.view"),
      fraudView: has("fraud.flag.view"),
      fraudDecide: has("fraud.review.decide"),
      blacklistView: has("fraud.blacklist.view"),
      blacklistCreate: has("fraud.blacklist.create"),
      blacklistDelete: has("fraud.blacklist.delete"),
      ruleView: has("fraud.rule.view"),
      ruleEdit: has("fraud.rule.edit"),
    };
  }, [me, branchId]);
}
