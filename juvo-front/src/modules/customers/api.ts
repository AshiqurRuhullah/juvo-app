"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { toCustomerQuery, type CustomerFilters } from "./logic";

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
