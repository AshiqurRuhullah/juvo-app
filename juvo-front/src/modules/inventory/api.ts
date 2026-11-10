"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";

/** Typed access to the inventory endpoints (Step 6-c). Every call goes through the BFF, which adds the token and the active branch. */
export type StockRow = Schemas["StockRowDto"];
export type StockMovement = Schemas["StockMovementDto"];
export type StockReason = Schemas["StockReason"];
export type Transfer = Schemas["TransferDto"];
export type TransferStatus = Schemas["TransferStatus"];
export type StockCountSummary = Schemas["CountListItemDto"];
export type StockCount = Schemas["CountDto"];
export type CountLine = Schemas["CountLineDto"];
export type CountStatus = Schemas["CountStatus"];
export type ProductStock = Schemas["ProductStockDto"];

/** Reasons staff may use by hand. Transfer and count reasons are written by the system only. */
export const MANUAL_REASONS = ["OpeningStock", "Correction", "Damage", "Loss", "Found", "CustomerReturn"] as const satisfies readonly StockReason[];
export type ManualReason = (typeof MANUAL_REASONS)[number];
export const ALL_REASONS: StockReason[] = [...MANUAL_REASONS, "TransferOut", "TransferIn", "TransferCancelled", "StockCount"];
export const TRANSFER_STATUSES: TransferStatus[] = ["Requested", "Approved", "InTransit", "Received", "Cancelled", "Rejected"];
export const COUNT_STATUSES: CountStatus[] = ["InProgress", "Submitted", "Approved", "Cancelled"];

/** Reasons that only add stock, only remove it, or either way (a correction). */
export const REASON_SIGN: Record<ManualReason, "add" | "remove" | "either"> = {
  OpeningStock: "add",
  Found: "add",
  CustomerReturn: "add",
  Damage: "remove",
  Loss: "remove",
  Correction: "either",
};

export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

/* ---- Queries -------------------------------------------------------------------------------------------------------------- */

export type StockFilters = { branchId?: string; productId?: string; lowOnly?: boolean };

export function useStock(params: ListParams & StockFilters) {
  return useQuery({
    queryKey: ["inventory", "stock", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/inventory/stock", {
          params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, branchId: params.branchId, productId: params.productId, lowOnly: params.lowOnly }) },
        }),
      ),
  });
}

/** Every variant of one product against every branch the user may view (zero where nothing was ever stocked). */
export function useProductStock(productId: string | undefined) {
  return useQuery({
    queryKey: ["inventory", "product-stock", productId],
    enabled: !!productId,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/inventory/stock/products/{productId}", { params: { path: { productId: productId! } } })),
  });
}

export type MovementFilters = { branchId?: string; reason?: StockReason; from?: string; to?: string; productId?: string; variantId?: string };

export function useMovements(params: ListParams & MovementFilters) {
  return useQuery({
    queryKey: ["inventory", "movements", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/inventory/movements", {
          params: {
            query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, branchId: params.branchId, reason: params.reason, from: params.from, to: params.to, productId: params.productId, variantId: params.variantId }),
          },
        }),
      ),
  });
}

export type TransferFilters = { status?: TransferStatus; branchId?: string };

export function useTransfers(params: ListParams & TransferFilters) {
  return useQuery({
    queryKey: ["inventory", "transfers", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/inventory/transfers", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, status: params.status, branchId: params.branchId }) } })),
  });
}

export function useTransfer(id: string) {
  return useQuery({ queryKey: ["inventory", "transfer", id], enabled: !!id, queryFn: async () => unwrap(await api.GET("/api/v1/inventory/transfers/{id}", { params: { path: { id } } })) });
}

export type CountFilters = { status?: CountStatus; branchId?: string };

export function useCounts(params: ListParams & CountFilters) {
  return useQuery({
    queryKey: ["inventory", "counts", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/inventory/counts", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, status: params.status, branchId: params.branchId }) } })),
  });
}

export function useCount(id: string) {
  return useQuery({ queryKey: ["inventory", "count", id], enabled: !!id, queryFn: async () => unwrap(await api.GET("/api/v1/inventory/counts/{id}", { params: { path: { id } } })) });
}

/* ---- Mutations ------------------------------------------------------------------------------------------------------------ */

/** Stock changes touch lists, ledger, transfers, counts and the product pages that show stock, so all of them refresh. */
function useInventoryMutation<TVars, TData>(run: (vars: TVars) => Promise<TData>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ["inventory"] }), queryClient.invalidateQueries({ queryKey: ["catalog"] })]),
  });
}

export const useAdjustStock = () =>
  useInventoryMutation(async (body: Schemas["AdjustStockRequest"]) => unwrap(await api.POST("/api/v1/inventory/adjustments", { body })));

export const useSetReorderLevel = () =>
  useInventoryMutation(async (body: Schemas["SetReorderLevelRequest"]) => unwrap(await api.PUT("/api/v1/inventory/reorder-levels", { body })));

export const useRequestTransfer = () =>
  useInventoryMutation(async (body: Schemas["RequestTransferRequest"]) => unwrap(await api.POST("/api/v1/inventory/transfers", { body })));

export type TransferAction = "approve" | "reject" | "ship" | "receive" | "cancel";
export const useTransferAction = (id: string) =>
  useInventoryMutation(async (vars: { action: TransferAction; reason?: string }) => {
    const params = { params: { path: { id } } };
    const body = { reason: vars.reason?.trim() || null };
    switch (vars.action) {
      case "approve":
        return unwrap(await api.POST("/api/v1/inventory/transfers/{id}/approve", params));
      case "reject":
        return unwrap(await api.POST("/api/v1/inventory/transfers/{id}/reject", { ...params, body }));
      case "ship":
        return unwrap(await api.POST("/api/v1/inventory/transfers/{id}/ship", params));
      case "receive":
        return unwrap(await api.POST("/api/v1/inventory/transfers/{id}/receive", params));
      case "cancel":
        return unwrap(await api.POST("/api/v1/inventory/transfers/{id}/cancel", { ...params, body }));
    }
  });

export const useStartCount = () =>
  useInventoryMutation(async (body: Schemas["StartCountRequest"]) => unwrap(await api.POST("/api/v1/inventory/counts", { body })));

export const useSaveCountLines = (id: string) =>
  useInventoryMutation(async (lines: { variantId: string; countedQuantity: number | null }[]) =>
    unwrap(await api.PUT("/api/v1/inventory/counts/{id}/lines", { params: { path: { id } }, body: { lines } })),
  );

export type CountAction = "submit" | "approve" | "reopen" | "cancel";
export const useCountAction = (id: string) =>
  useInventoryMutation(async (action: CountAction) => {
    const params = { params: { path: { id } } };
    switch (action) {
      case "submit":
        return unwrap(await api.POST("/api/v1/inventory/counts/{id}/submit", params));
      case "approve":
        return unwrap(await api.POST("/api/v1/inventory/counts/{id}/approve", params));
      case "reopen":
        return unwrap(await api.POST("/api/v1/inventory/counts/{id}/reopen", params));
      case "cancel":
        return unwrap(await api.POST("/api/v1/inventory/counts/{id}/cancel", params));
    }
  });
