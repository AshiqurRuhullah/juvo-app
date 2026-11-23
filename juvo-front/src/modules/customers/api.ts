"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { downloadFile } from "@/modules/reports/api";
import { toCustomerQuery, type CustomerFilters } from "./logic";
import { exportFallbackName } from "./privacy-logic";

/** Typed access to the staff customer endpoints (docs/storefront-api.md, "Staff endpoints"). Every call goes through the BFF. */
export type CustomerListItem = Schemas["CustomerListItemDto"];
export type CustomerDetail = Schemas["CustomerDetailDto"];
export type CustomerAddress = Schemas["AddressDto"];
export type CustomerOrderStats = Schemas["CustomerOrderStatsDto"];

const KEY = ["customers"] as const;

export function useCustomers(args: { filters: CustomerFilters; page: number; pageSize: number; search?: string }) {
  const query = toCustomerQuery(args.filters, { page: args.page, pageSize: args.pageSize, search: args.search });
  return useQuery({
    queryKey: [...KEY, "list", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/customers", { params: { query: query as never } })),
  });
}

export function useCustomer(id: string) {
  return useQuery({
    queryKey: [...KEY, "detail", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/customers/{id}", { params: { path: { id } } })),
  });
}

/** Order history numbers (needs `fraud.flag.view`; the screen asks only when the person holds it). */
export function useCustomerOrderStats(id: string, enabled: boolean) {
  return useQuery({
    queryKey: ["fraud", "stats", id],
    enabled: enabled && !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/fraud/customers/{customerId}/stats", { params: { path: { customerId: id } } })),
  });
}

/** Deactivate or activate. The API answers with the changed row; the detail and every list are read again. */
export function useSetCustomerActive(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (active: boolean) =>
      active ? unwrap(await api.POST("/api/v1/customers/{id}/activate", { params: { path: { id } } })) : unwrap(await api.POST("/api/v1/customers/{id}/deactivate", { params: { path: { id } } })),
    onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: [...KEY, "list"] }), queryClient.invalidateQueries({ queryKey: [...KEY, "detail", id] })]),
  });
}

export type ConsentHistory = Schemas["ConsentHistoryDto"];
export type AnonymiseResult = Schemas["AnonymiseResultDto"];

/** The consent change log of one customer (needs `customers.customer.view`; IP addresses arrive partly hidden without view-pii). */
export function useConsentHistory(id: string, enabled: boolean) {
  return useQuery({
    queryKey: [...KEY, "consent-history", id],
    enabled: enabled && !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/customers/{id}/consents/history", { params: { path: { id } } })),
  });
}

/** Downloads the customer's data as a JSON file (needs `customers.privacy.export`). A refusal, such as the hourly limit, throws an ApiError. */
export const exportCustomerData = (id: string) =>
  downloadFile(`/api/bff/api/v1/customers/${encodeURIComponent(id)}/privacy/export`, exportFallbackName(id), { method: "POST" });

/** Irreversible anonymisation (needs `customers.privacy.anonymise`). The customer, the list and the history are read again afterwards. */
export function useAnonymiseCustomer(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: { reason: string; confirm: string }) => unwrap(await api.POST("/api/v1/customers/{id}/privacy/anonymise", { params: { path: { id } }, body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: KEY }),
  });
}
