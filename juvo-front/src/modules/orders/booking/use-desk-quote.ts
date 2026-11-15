"use client";

import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { useDebounced } from "@/lib/hooks";
import { buildQuoteBody, canQuote, type DeskQuoteInput } from "./logic";

export type ManualQuote = Schemas["ManualQuoteDto"];

/**
 * The order desk's real preview (`POST /orders/manual/quote`, the same pricing code as creating the order). The request, as text,
 * must stay unchanged for 400 ms before it is sent, so typing a discount or a quantity does not send a call per key. The request is
 * the query key: a slow answer for an old request can never replace the answer for the current one, and an old request still in
 * flight is aborted by the query's signal.
 */
export function useDeskQuote(input: DeskQuoteInput) {
  const ready = canQuote(input);
  const wanted = ready ? JSON.stringify(buildQuoteBody(input)) : "";
  const settled = useDebounced(wanted, 400);
  const query = useQuery({
    queryKey: ["desk", "quote", settled],
    enabled: ready && settled !== "",
    retry: false,
    staleTime: 0,
    gcTime: 30_000,
    // The last numbers stay on screen while the next ones load, instead of flashing empty.
    placeholderData: keepPreviousData,
    queryFn: async ({ signal }) => unwrap(await api.POST("/api/v1/orders/manual/quote", { body: JSON.parse(settled) as never, signal })),
  });
  // True while the figures on screen belong to an older version of the form (waiting for the pause, or loading).
  const settling = ready && (settled !== wanted || query.isFetching);
  return { ...query, ready, settling };
}
