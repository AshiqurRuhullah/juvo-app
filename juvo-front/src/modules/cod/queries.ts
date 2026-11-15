"use client";

import { keepPreviousData, useQueries, useQuery } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import { COURIER_KEYS, clean, useCouriers, type Courier } from "@/modules/couriers/api";
import { num, pendingAmount, round2 } from "./logic";

/**
 * Extra reads for the COD screens that the shared courier hooks do not cover. They use the same cache keys as `usePendingCod` and
 * `useSettlements`, so a refresh after any settlement action updates them too.
 */

/** One small request per status: the total is the count, the rows (for the working states) give the sums. Run in parallel. */
export function useStatusSummary(branchId: string | undefined, courierId: string | undefined) {
  const results = useQueries({
    queries: (
      [
        { status: "Open", pageSize: 100 },
        { status: "Reconciled", pageSize: 100 },
        { status: "Settled", pageSize: 1 },
        { status: "Cancelled", pageSize: 1 },
      ] as const
    ).map((q) => ({
      queryKey: [...COURIER_KEYS.settlements, "list", { page: 1, pageSize: q.pageSize, status: q.status, branchId, courierId, summary: true }],
      placeholderData: keepPreviousData,
      staleTime: 15_000,
      queryFn: async () =>
        unwrap(await api.GET("/api/v1/couriers/cod-settlements", { params: { query: { Page: 1, PageSize: q.pageSize, ...clean({ status: q.status, branchId, courierId }) } as never } })),
    })),
  });
  const [open, reconciled, settled, cancelled] = results;
  const pending = results.some((r) => r.isPending);
  const sum = (rows: { expectedTotal: number | string }[] | undefined) => round2((rows ?? []).reduce((acc, r) => acc + num(r.expectedTotal), 0));
  const unsettledRows = [...(open?.data?.items ?? []), ...(reconciled?.data?.items ?? [])];
  const unsettledCount = num(open?.data?.total) + num(reconciled?.data?.total);
  return {
    pending,
    error: results.some((r) => r.isError),
    counts: { Open: num(open?.data?.total), Reconciled: num(reconciled?.data?.total), Settled: num(settled?.data?.total), Cancelled: num(cancelled?.data?.total) },
    /** The expected cash of batches that are not settled yet. `partial` when more batches exist than were read (the sum is then a lower bound). */
    unsettledTotal: round2(sum(open?.data?.items) + sum(reconciled?.data?.items)),
    unsettledCount,
    partial: unsettledRows.length < unsettledCount,
  };
}

/** Cash still at each courier: one pending request per courier that takes cash, all started together once the couriers are known. */
export function useCashAtCouriers(branchId: string | undefined) {
  const couriers = useCouriers();
  const list: Courier[] = (couriers.data ?? []).filter((c) => c.codSupported);
  const results = useQueries({
    queries: list.map((c) => ({
      queryKey: [...COURIER_KEYS.settlements, "pending", c.id, branchId ?? null],
      staleTime: 15_000,
      queryFn: async () => unwrap(await api.GET("/api/v1/couriers/cod-settlements/pending", { params: { query: { courierId: c.id, ...(branchId ? { branchId } : {}) } as never } })),
    })),
  });
  const rows = list
    .map((c, i) => {
      const items = results[i]?.data ?? [];
      return { courier: c, parcels: items.length, amount: round2(items.reduce((acc, p) => acc + pendingAmount(p), 0)) };
    })
    .filter((r) => r.parcels > 0);
  return {
    pending: couriers.isPending || results.some((r) => r.isPending),
    rows,
    parcels: rows.reduce((a, r) => a + r.parcels, 0),
    amount: round2(rows.reduce((a, r) => a + r.amount, 0)),
  };
}

/** Parcels waiting for a batch for one courier and branch (the new-batch screen). */
export function usePendingParcels(courierId: string | undefined, branchId: string | undefined) {
  return useQuery({
    queryKey: [...COURIER_KEYS.settlements, "pending", courierId, branchId ?? null],
    enabled: !!courierId,
    staleTime: 0,
    queryFn: async () => unwrap(await api.GET("/api/v1/couriers/cod-settlements/pending", { params: { query: { courierId: courierId!, ...(branchId ? { branchId } : {}) } as never } })),
  });
}
