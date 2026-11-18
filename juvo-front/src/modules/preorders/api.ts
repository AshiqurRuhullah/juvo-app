"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";

/** Typed access to the staff pre-order endpoints (queue, allocation, product and variant settings). Every call goes through the BFF. */
export type PreorderQueueItem = Schemas["PreorderQueueItemDto"];
export type PreorderLine = Schemas["PreorderLineDto"];
export type PreorderSettings = Schemas["PreorderSettingsDto"];
export type PreorderSettingsRequest = Schemas["PreorderSettingsRequest"];
export type VariantPreorder = Schemas["VariantPreorderDto"];
export type AllocationResult = Schemas["AllocationResultDto"];
export type PreorderStateName = Schemas["PreorderState"];

export const PREORDER_KEYS = {
  all: ["preorders"] as const,
  queue: ["preorders", "queue"] as const,
  settings: (productId: string) => ["preorders", "settings", productId] as const,
};

export const PREORDER_STATES: PreorderStateName[] = ["Waiting", "Ready"];

export type QueueFilter = { page?: number; pageSize?: number; search?: string; state?: string; branchId?: string; expectedBefore?: string };

const clean = <T extends Record<string, unknown>>(value: T) => Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null)) as Partial<T>;

export function usePreorderQueue(filter: QueueFilter) {
  const { page, pageSize, search, ...rest } = filter;
  return useQuery({
    queryKey: [...PREORDER_KEYS.queue, filter],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/orders/preorders", {
          params: { query: { Page: page, PageSize: pageSize, Search: search || undefined, ...clean(rest) } as never },
        }),
      ),
  });
}

/** Give available stock to the waiting lines, oldest first. Null branch or variant means all the caller may serve. */
export function useAllocatePreorders() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: { branchId: string | null; variantId: string | null }) => unwrap(await api.POST("/api/v1/orders/preorders/allocate", { body })),
    onSuccess: async () => {
      await Promise.all([queryClient.invalidateQueries({ queryKey: PREORDER_KEYS.all }), queryClient.invalidateQueries({ queryKey: ["orders"] })]);
    },
  });
}

export function usePreorderSettings(productId: string, enabled = true) {
  return useQuery({
    queryKey: PREORDER_KEYS.settings(productId),
    enabled: enabled && !!productId,
    queryFn: async () => unwrap(await api.GET("/api/v1/catalog/products/{id}/preorder", { params: { path: { id: productId } } })),
  });
}

export function useSaveProductPreorder(productId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: PreorderSettingsRequest) => unwrap(await api.PUT("/api/v1/catalog/products/{id}/preorder", { params: { path: { id: productId } }, body })),
    onSuccess: (data) => queryClient.setQueryData(PREORDER_KEYS.settings(productId), data),
  });
}

export function useSaveVariantPreorder(productId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { variantId: string; body: PreorderSettingsRequest }) =>
      unwrap(await api.PUT("/api/v1/catalog/products/{id}/variants/{variantId}/preorder", { params: { path: { id: productId, variantId: vars.variantId } }, body: vars.body })),
    onSuccess: (data) => queryClient.setQueryData(PREORDER_KEYS.settings(productId), data),
  });
}
