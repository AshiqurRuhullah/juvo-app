"use client";

import { useQuery } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";

/** Delivery zones for the courier form's "serves these zones" choice. Asked only while the form is open; a person without the zone
 *  permission gets an error and the form falls back to typing zone codes. Zones rarely change, so the answer is kept for five minutes. */
export function useDeliveryZones(enabled: boolean) {
  return useQuery({
    queryKey: ["couriers", "zones"],
    enabled,
    staleTime: 5 * 60_000,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/delivery/zones")),
  });
}
