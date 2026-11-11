"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";

/** Typed access to the returns endpoints (module L, docs/returns-api.md). Every call goes through the BFF. */
export type Eligibility = Schemas["ReturnEligibilityDto"];
export type ReturnableLine = Schemas["ReturnableLineDto"];
export type ReturnReason = Schemas["ReturnReasonDto"];
export type MyReturn = Schemas["MyReturnDto"];
export type MyReturnListItem = Schemas["MyReturnListItemDto"];
export type ReturnDetail = Schemas["ReturnDetailDto"];
export type ReturnItem = Schemas["ReturnItemDto"];
export type ReturnListItem = Schemas["ReturnListItemDto"];
export type ReturnSummary = Schemas["ReturnSummaryDto"];
export type ReturnPhoto = Schemas["ReturnPhotoDto"];
export type BulkResult = Schemas["BulkReturnResult"];
export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

/* ---- Customer -------------------------------------------------------------------------------------------------------------- */

export function useEligibility(orderId: string | undefined) {
  return useQuery({
    queryKey: ["returns", "eligibility", orderId],
    enabled: !!orderId,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/orders/{id}/return-eligibility", { params: { path: { id: orderId! } } })),
  });
}

/** One picture per call through the BFF (the typed client is JSON only). Throws an ApiError with the API's problem. */
export async function uploadReturnPhoto(file: File): Promise<ReturnPhoto & { path: string }> {
  const form = new FormData();
  form.append("file", file);
  const response = await fetch("/api/bff/api/v1/me/returns/photos", { method: "POST", body: form, credentials: "same-origin" });
  const text = await response.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;
  if (!response.ok) throw new ApiError(toProblem(parsed, response.status));
  return parsed as ReturnPhoto;
}

export type CreateReturn = Omit<Schemas["CreateReturnRequest"], "idempotencyKey" | "details" | "items" | "photoPaths"> & {
  details?: string | null;
  items: Schemas["CreateReturnLine"][];
  photoPaths: string[];
};

/** Sends the request with an Idempotency-Key: the same key answers with the same return, so a retry never files it twice. */
export function useCreateReturn() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { body: CreateReturn; key: string }) =>
      unwrap(
        await api.POST("/api/v1/me/returns", {
          body: { ...vars.body, details: vars.body.details?.trim() || null, idempotencyKey: vars.key },
          headers: { "Idempotency-Key": vars.key },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["returns"] }),
  });
}

export type MyReturnsFilter = "active" | "closed";

export function useMyReturns(params: { filter: MyReturnsFilter; page: number; pageSize: number }) {
  return useQuery({
    queryKey: ["returns", "mine", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/returns", { params: { query: { filter: params.filter, Page: params.page, PageSize: params.pageSize } } })),
  });
}

export function useMyReturn(id: string) {
  return useQuery({
    queryKey: ["returns", "mine-one", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/returns/{id}", { params: { path: { id } } })),
  });
}

export function useCancelMyReturn(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (reason: string) => unwrap(await api.POST("/api/v1/me/returns/{id}/cancel", { params: { path: { id } }, body: { reason: reason.trim() || null } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["returns"] }),
  });
}

/* ---- Staff ----------------------------------------------------------------------------------------------------------------- */

export type QueueFilters = { status?: Schemas["ReturnStatus"]; kind?: Schemas["ReturnKind"]; fault?: Schemas["ReturnFault"]; branchId?: string; from?: string; to?: string };

export function useReturns(params: ListParams & QueueFilters) {
  return useQuery({
    queryKey: ["returns", "queue", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/returns", {
          params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, status: params.status, kind: params.kind, fault: params.fault, branchId: params.branchId, from: params.from, to: params.to }) },
        }),
      ),
  });
}

export const useReturnSummary = () => useQuery({ queryKey: ["returns", "summary"], queryFn: async () => unwrap(await api.GET("/api/v1/returns/summary")) });

export function useReturn(id: string) {
  return useQuery({
    queryKey: ["returns", "detail", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/returns/{id}", { params: { path: { id } } })),
  });
}

export type ActionVars =
  | { action: "approve"; note?: string }
  | { action: "reject"; reason: string }
  | { action: "schedulePickup"; date: string; courier?: string; note?: string }
  | { action: "pickedUp" }
  | { action: "received" }
  | { action: "inspect"; body: Schemas["InspectReturnRequest"] }
  | { action: "refund"; method: Schemas["RefundMethod"]; reference: string; amount: number }
  | { action: "exchange"; lines?: Schemas["ExchangeLine"][]; note?: string }
  | { action: "rejectRefund"; reason: string };

/** One stage step. Every endpoint answers the updated detail, which replaces the cached one. */
export function useReturnAction(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: ActionVars): Promise<ReturnDetail> => {
      const params = { params: { path: { id } } };
      switch (vars.action) {
        case "approve":
          return unwrap(await api.POST("/api/v1/returns/{id}/approve", { ...params, body: { note: vars.note?.trim() || null, useCourier: false } }));
        case "reject":
          return unwrap(await api.POST("/api/v1/returns/{id}/reject", { ...params, body: { reason: vars.reason.trim() } }));
        case "schedulePickup":
          return unwrap(await api.POST("/api/v1/returns/{id}/schedule-pickup", { ...params, body: { date: vars.date, courier: vars.courier?.trim() || null, note: vars.note?.trim() || null, useCourier: false } }));
        case "pickedUp":
          return unwrap(await api.POST("/api/v1/returns/{id}/picked-up", params));
        case "received":
          return unwrap(await api.POST("/api/v1/returns/{id}/received", params));
        case "inspect":
          return unwrap(await api.POST("/api/v1/returns/{id}/inspect", { ...params, body: vars.body }));
        case "refund":
          return unwrap(await api.POST("/api/v1/returns/{id}/refund", { ...params, body: { method: vars.method, reference: vars.reference.trim(), amount: vars.amount } }));
        case "exchange":
          return unwrap(await api.POST("/api/v1/returns/{id}/exchange", { ...params, body: { lines: vars.lines ?? null, note: vars.note?.trim() || null, useCourier: false } }));
        case "rejectRefund":
          return unwrap(await api.POST("/api/v1/returns/{id}/reject-refund", { ...params, body: { reason: vars.reason.trim() } }));
      }
    },
    onSuccess: (detail) => {
      queryClient.setQueryData(["returns", "detail", id], detail);
      return Promise.all([queryClient.invalidateQueries({ queryKey: ["returns", "queue"] }), queryClient.invalidateQueries({ queryKey: ["returns", "summary"] }), queryClient.invalidateQueries({ queryKey: ["inventory"] })]);
    },
  });
}

export function useAddReturnNote(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (text: string) => unwrap(await api.POST("/api/v1/returns/{id}/notes", { params: { path: { id } }, body: { text: text.trim() } })),
    onSuccess: (detail) => queryClient.setQueryData(["returns", "detail", id], detail),
  });
}

export function useBulkReturns() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { action: "approve" | "reject"; ids: string[]; reason?: string }) =>
      unwrap(await api.POST("/api/v1/returns/bulk", { body: { action: vars.action, ids: vars.ids, reason: vars.reason?.trim() || null } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["returns"] }),
  });
}
