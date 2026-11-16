"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, api, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";

/** Typed access to suppliers, purchase orders, goods receipts and suggestions (juvo-be PurchasingEndpoints). Every call goes through the BFF. */
export type Supplier = Schemas["SupplierDto"];
export type SaveSupplier = Schemas["SaveSupplierRequest"];
export type PurchaseListItem = Schemas["PurchaseListItemDto"];
export type PurchaseDetail = Schemas["PurchaseDetailDto"];
export type PurchaseLine = Schemas["PurchaseLineDto"];
export type SavePurchaseOrder = Schemas["SavePurchaseOrderRequest"];
export type ReceiveGoods = Schemas["ReceiveGoodsRequest"];
export type GoodsReceipt = Schemas["GoodsReceiptDto"];
export type Suggestions = Schemas["SuggestionsDto"];
export type SuggestedLine = Schemas["SuggestedLineDto"];
export type PurchasePrint = Schemas["PurchasePrintDto"];
export type AttachmentUpload = Schemas["AttachmentUploadDto"];

export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

/** Drops empty filter values so the request holds only what the person chose. */
export const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

/** Everything about purchasing shares this root, so one call refreshes every open screen. */
export const PURCHASING_KEYS = { all: ["purchasing"] as const };

export function useInvalidatePurchasing() {
  const queryClient = useQueryClient();
  return () => {
    void queryClient.invalidateQueries({ queryKey: PURCHASING_KEYS.all });
    // Receiving moves stock and may change product costs, so the stock and product screens are stale too.
    void queryClient.invalidateQueries({ queryKey: ["inventory"] });
    void queryClient.invalidateQueries({ queryKey: ["accounting"] });
  };
}

/* ---- Suppliers ------------------------------------------------------------------------------------------------------------- */

export type SupplierFilter = ListParams & { active?: boolean };

export function useSuppliers(filter: SupplierFilter, options: { enabled?: boolean } = {}) {
  const { page, pageSize, ...rest } = filter;
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "suppliers", filter],
    placeholderData: keepPreviousData,
    enabled: options.enabled ?? true,
    queryFn: async () => unwrap(await api.GET("/api/v1/suppliers", { params: { query: { Page: page, PageSize: pageSize, ...clean(rest) } as never } })),
  });
}

/** Active suppliers for a picker (the first 100 by name). Mounted only by screens that need the list. */
export function useSupplierOptions(enabled = true) {
  return useSuppliers({ page: 1, pageSize: 100, active: true, sort: "name" }, { enabled });
}

export function useSupplier(id: string | undefined) {
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "supplier", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/suppliers/{id}", { params: { path: { id: id! } } })),
  });
}

export function useSaveSupplier() {
  const invalidate = useInvalidatePurchasing();
  return useMutation({
    mutationFn: async (vars: { id?: string; body: SaveSupplier }) =>
      vars.id
        ? unwrap(await api.PUT("/api/v1/suppliers/{id}", { params: { path: { id: vars.id } }, body: vars.body }))
        : unwrap(await api.POST("/api/v1/suppliers", { body: vars.body })),
    onSuccess: invalidate,
  });
}

/* ---- Purchase orders ------------------------------------------------------------------------------------------------------- */

export type OrderFilter = ListParams & {
  status?: string;
  paymentStatus?: string;
  supplierId?: string;
  branchId?: string;
  overdue?: boolean;
  payable?: boolean;
  from?: string;
  to?: string;
};

export function usePurchaseOrders(filter: OrderFilter) {
  const { page, pageSize, ...rest } = filter;
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "orders", filter],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders", { params: { query: { Page: page, PageSize: pageSize, ...clean(rest) } as never } })),
  });
}

export function usePurchaseOrder(id: string | undefined) {
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "order", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders/{id}", { params: { path: { id: id! } } })),
  });
}

export function usePurchasePrint(id: string) {
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "print", id],
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders/{id}/print", { params: { path: { id } } })),
  });
}

export function useSavePurchaseOrder() {
  const invalidate = useInvalidatePurchasing();
  return useMutation({
    mutationFn: async (vars: { id?: string; body: SavePurchaseOrder }) =>
      vars.id
        ? unwrap(await api.PUT("/api/v1/purchasing/orders/{id}", { params: { path: { id: vars.id } }, body: vars.body }))
        : unwrap(await api.POST("/api/v1/purchasing/orders", { body: vars.body })),
    onSuccess: invalidate,
  });
}

export type OrderAction =
  | { kind: "send" }
  | { kind: "cancel"; reason: string }
  | { kind: "closeShort"; reason: string }
  | { kind: "invoice"; number: string; invoiceDate: string; dueDate?: string | null; amount?: number | null }
  | { kind: "pay"; amount: number; method: "Cash" | "Bank" | "MobileBanking" | "Cheque" | "Other"; reference?: string | null; paidOn?: string | null; accountId?: string | null; note?: string | null }
  | { kind: "attach"; paths: string[] };

/** One mutation for every step of a purchase order that answers with the new order. */
export function useOrderAction(id: string) {
  const invalidate = useInvalidatePurchasing();
  return useMutation({
    mutationFn: async (action: OrderAction): Promise<PurchaseDetail> => {
      const path = { params: { path: { id } } };
      switch (action.kind) {
        case "send":
          return unwrap(await api.POST("/api/v1/purchasing/orders/{id}/send", path));
        case "cancel":
          return unwrap(await api.POST("/api/v1/purchasing/orders/{id}/cancel", { ...path, body: { reason: action.reason } }));
        case "closeShort":
          return unwrap(await api.POST("/api/v1/purchasing/orders/{id}/close-short", { ...path, body: { reason: action.reason } }));
        case "invoice":
          return unwrap(await api.POST("/api/v1/purchasing/orders/{id}/invoice", { ...path, body: { number: action.number, invoiceDate: action.invoiceDate, dueDate: action.dueDate ?? null, amount: action.amount ?? null } }));
        case "pay":
          return unwrap(await api.POST("/api/v1/purchasing/orders/{id}/payments", { ...path, body: { amount: action.amount, method: action.method, reference: action.reference ?? null, paidOn: action.paidOn ?? null, accountId: action.accountId ?? null, note: action.note ?? null } }));
        case "attach":
          return unwrap(await api.POST("/api/v1/purchasing/orders/{id}/attachments", { ...path, body: { attachmentPaths: action.paths } }));
      }
    },
    onSuccess: invalidate,
  });
}

export function useReceiveGoods(id: string) {
  const invalidate = useInvalidatePurchasing();
  return useMutation({
    mutationFn: async (body: ReceiveGoods) => unwrap(await api.POST("/api/v1/purchasing/orders/{id}/receive", { params: { path: { id } }, body })),
    onSuccess: invalidate,
  });
}

/** Multipart upload of a supplier document through the BFF (the typed client is JSON only). Throws an ApiError with the API's problem. */
export async function uploadPurchaseFile(file: File): Promise<AttachmentUpload> {
  const form = new FormData();
  form.append("file", file);
  const response = await fetch("/api/bff/api/v1/purchasing/orders/uploads", { method: "POST", body: form, credentials: "same-origin" });
  const text = await response.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;
  if (!response.ok) throw new ApiError(toProblem(parsed, response.status));
  return parsed as AttachmentUpload;
}

/* ---- Receipts -------------------------------------------------------------------------------------------------------------- */

export type ReceiptFilter = ListParams & { purchaseOrderId?: string; branchId?: string; from?: string; to?: string };

export function useReceipts(filter: ReceiptFilter) {
  const { page, pageSize, ...rest } = filter;
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "receipts", filter],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts", { params: { query: { Page: page, PageSize: pageSize, ...clean(rest) } as never } })),
  });
}

export function useReceipt(id: string | null) {
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "receipt", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/receipts/{id}", { params: { path: { id: id! } } })),
  });
}

export function useApplyCost() {
  const invalidate = useInvalidatePurchasing();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/purchasing/receipts/{id}/apply-cost", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

/* ---- Suggestions ----------------------------------------------------------------------------------------------------------- */

export function useSuggestions(branchId: string | undefined) {
  return useQuery({
    queryKey: [...PURCHASING_KEYS.all, "suggestions", branchId],
    enabled: !!branchId,
    queryFn: async () => unwrap(await api.GET("/api/v1/purchasing/orders/suggestions", { params: { query: { branchId } } })),
  });
}
