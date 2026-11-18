"use client";

import * as React from "react";
import { useQuery } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";

/**
 * Which switchable features are on, for the menu. Only the customer area asks (one small call, cached for a minute): the rewards
 * entry exists only while the API says the rewards feature is on. The call carries the probe header, so the BFF turns the API's
 * "409 LOYALTY.FEATURE_OFF" into a plain 200 { enabled: false } and the browser logs no failed request on every account page.
 * Staff always see their `Loyalty` screens, so the shop can set the rules up first.
 */
export function useNavFeatures(area: "admin" | "account"): { loyalty: boolean } {
  const probe = useQuery({
    queryKey: ["loyalty", "nav-probe"],
    enabled: area === "account",
    retry: false,
    staleTime: 60_000,
    queryFn: async (): Promise<boolean> => {
      const summary = unwrap(await api.GET("/api/v1/me/rewards", { headers: { "x-juvo-probe": "feature" } })) as { enabled?: boolean };
      return summary.enabled === true;
    },
  });
  const on = area === "account" && probe.data === true;
  return React.useMemo(() => ({ loyalty: on }), [on]);
}
