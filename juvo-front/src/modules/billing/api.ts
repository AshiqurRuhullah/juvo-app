"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";

/**
 * Typed access to tax rules, invoices and credit notes (Step 9-a, module U). Staff screens use /billing; the customer's own
 * invoice uses /me/orders/{id}/invoice and, for a guest, /store/orders/invoice with the tracking token.
 */
export type TaxRule = Schemas["TaxRuleDto"];
export type SaveTaxRule = Schemas["SaveTaxRuleRequest"];
export type Invoice = Schemas["InvoiceDto"];
export type InvoiceListItem = Schemas["InvoiceListItemDto"];
export type CreditNote = Schemas["CreditNoteDto"];

const clean = <T extends Record<string, unknown>>(value: T) => Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

export type ListParams = { page: number; pageSize: number; sort?: string; search?: string; branchId?: string; from?: string; to?: string };

export const useTaxRules = () => useQuery({ queryKey: ["billing", "rules"], queryFn: async () => unwrap(await api.GET("/api/v1/billing/tax-rules")) });

function useBillingMutation<TVars, TData>(run: (vars: TVars) => Promise<TData>) {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: run, onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ["billing"] }), queryClient.invalidateQueries({ queryKey: ["orders"] })]) });
}

export const useCreateTaxRule = () => useBillingMutation(async (body: SaveTaxRule) => unwrap(await api.POST("/api/v1/billing/tax-rules", { body })));
export const useUpdateTaxRule = () => useBillingMutation(async (vars: { id: string; body: SaveTaxRule }) => unwrap(await api.PUT("/api/v1/billing/tax-rules/{id}", { params: { path: { id: vars.id } }, body: vars.body })));
export const useActivateTaxRule = () =>
  useBillingMutation(async (vars: { id: string; acknowledgeUnverified: boolean }) => unwrap(await api.POST("/api/v1/billing/tax-rules/{id}/activate", { params: { path: { id: vars.id } }, body: { acknowledgeUnverified: vars.acknowledgeUnverified } })));
export const useDeactivateTaxRule = () => useBillingMutation(async (id: string) => unwrap(await api.POST("/api/v1/billing/tax-rules/{id}/deactivate", { params: { path: { id } } })));

export function useInvoices(params: ListParams) {
  return useQuery({
    queryKey: ["billing", "invoices", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/billing/invoices", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, branchId: params.branchId, from: params.from, to: params.to }) } })),
  });
}
export const useInvoice = (id: string) => useQuery({ queryKey: ["billing", "invoice", id], queryFn: async () => unwrap(await api.GET("/api/v1/billing/invoices/{id}", { params: { path: { id } } })) });
export const useIssueInvoice = () => useBillingMutation(async (orderId: string) => unwrap(await api.POST("/api/v1/billing/orders/{orderId}/invoice", { params: { path: { orderId } } })));

export function useCreditNotes(params: ListParams) {
  return useQuery({
    queryKey: ["billing", "credit-notes", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/billing/credit-notes", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, branchId: params.branchId, from: params.from, to: params.to }) } })),
  });
}
export const useCreditNote = (id: string) => useQuery({ queryKey: ["billing", "credit-note", id], queryFn: async () => unwrap(await api.GET("/api/v1/billing/credit-notes/{id}", { params: { path: { id } } })) });

/** Download addresses (plain links through the BFF; the browser sends the cookies). */
export const invoicePdfUrl = (id: string, lang: string) => `/api/bff/api/v1/billing/invoices/${encodeURIComponent(id)}/pdf?lang=${lang}`;
export const creditNotePdfUrl = (id: string, lang: string) => `/api/bff/api/v1/billing/credit-notes/${encodeURIComponent(id)}/pdf?lang=${lang}`;
export const myInvoicePdfUrl = (orderId: string, lang: string) => `/api/bff/api/v1/me/orders/${encodeURIComponent(orderId)}/invoice/pdf?lang=${lang}`;
export const guestInvoicePdfUrl = (number: string, token: string, lang: string) => `/api/bff/api/v1/store/orders/invoice/pdf?number=${encodeURIComponent(number)}&token=${encodeURIComponent(token)}&lang=${lang}`;

/** My invoice (signed-in customer). 404 or 409 simply means none yet (not confirmed): no error is shown for it. */
export const useMyInvoice = (orderId: string, enabled: boolean) =>
  useQuery({ queryKey: ["billing", "mine", orderId], enabled, retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/me/orders/{id}/invoice", { params: { path: { id: orderId } } })) });

/** A guest's invoice, proven by the tracking token. */
export const useGuestInvoice = (number: string, token: string | null, enabled: boolean) =>
  useQuery({
    queryKey: ["billing", "guest", number, token],
    enabled: enabled && !!token,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/store/orders/invoice", { params: { query: { number, token: token! } } })),
  });
