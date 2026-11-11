"use client";

import * as React from "react";
import { useMe } from "@/modules/store/api";
import { useStoreSession } from "@/modules/store/store-session";
import { ORDER_STORAGE_PREFIX } from "@/modules/orders/checkout-view";
import type { CheckoutResult, TrackQuery } from "@/modules/orders/api";
import { useSessionStorage } from "./common";

/**
 * How this browser proves an order to the payment pages: the tracking token (from the private link, or from the checkout answer
 * kept in this tab) or, for a signed-in customer, the account itself (the order is looked up by number and the account's mobile).
 */
export function useOrderAccess(number: string, tokenParam?: string | null) {
  const { customer } = useStoreSession();
  const storage = useSessionStorage();
  const me = useMe(customer);

  const stored = React.useMemo(() => {
    try {
      const raw = storage?.getItem(`${ORDER_STORAGE_PREFIX}${number}`);
      return raw ? (JSON.parse(raw) as CheckoutResult) : null;
    } catch {
      return null;
    }
  }, [storage, number]);

  const token = tokenParam || stored?.trackingToken || null;
  const mobile = me.data?.mobile ?? null;
  const query: TrackQuery | null = token ? { token } : customer && mobile ? { number, mobile } : null;
  // Waiting for the session or the account is not the same as having no way in.
  const settling = !token && customer && me.isPending;
  return { token, query, settling, customer, stored };
}
