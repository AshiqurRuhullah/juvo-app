"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";

/** Typed access to the couriers, shipments and COD settlement endpoints (docs/couriers-api.md). Every call goes through the BFF. */
export type Courier = Schemas["CourierDto"];
export type CourierProvider = Schemas["CourierProviderDto"];
export type SaveCourier = Schemas["SaveCourierRequest"];
export type Suggestion = Schemas["CourierSuggestionDto"];
export type Shipment = Schemas["ShipmentDto"];
export type ShipmentListItem = Schemas["ShipmentListItemDto"];
export type ShipmentEvent = Schemas["ShipmentEventDto"];
export type ShipmentLabel = Schemas["ShipmentLabelDto"];
export type BookRequest = Schemas["BookShipmentRequest"];
export type BulkBookResult = Schemas["BulkBookResult"];
export type Settlement = Schemas["SettlementDto"];
export type SettlementListItem = Schemas["SettlementListItemDto"];
export type SettlementItem = Schemas["SettlementItemDto"];

export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

/** Drops empty filter values so the URL stays short and the API sees only what the person chose. */
export const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

/** Everything that depends on parcels, couriers or settlements shares this root, so one call refreshes every open screen. */
export const COURIER_KEYS = { all: ["couriers"] as const, shipments: ["couriers", "shipments"] as const, settlements: ["couriers", "settlements"] as const };

export function useInvalidateCouriers() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: COURIER_KEYS.all });
}

/* ---- Couriers -------------------------------------------------------------------------------------------------------------- */

export function useCouriers() {
  return useQuery({
    queryKey: [...COURIER_KEYS.all, "list"],
    queryFn: async () => unwrap(await api.GET("/api/v1/couriers")),
  });
}

export function useCourierProviders() {
  return useQuery({
    queryKey: [...COURIER_KEYS.all, "providers"],
    staleTime: 5 * 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/couriers/providers")),
  });
}

export function useSaveCourier() {
  const invalidate = useInvalidateCouriers();
  return useMutation({
    mutationFn: async (vars: { id?: string; code?: string; body: SaveCourier }) =>
      vars.id
        ? unwrap(await api.PUT("/api/v1/couriers/{id}", { params: { path: { id: vars.id } }, body: vars.body }))
        : unwrap(await api.POST("/api/v1/couriers", { body: { ...vars.body, code: vars.code } as SaveCourier })),
    onSuccess: invalidate,
  });
}

export function useSetCourierEnabled() {
  const invalidate = useInvalidateCouriers();
  return useMutation({
    mutationFn: async (vars: { id: string; isEnabled: boolean }) =>
      unwrap(await api.PUT("/api/v1/couriers/{id}/enabled", { params: { path: { id: vars.id } }, body: { isEnabled: vars.isEnabled } })),
    onSuccess: invalidate,
  });
}

export function useArchiveCourier() {
  const invalidate = useInvalidateCouriers();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/couriers/{id}", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

/* ---- Shipments ------------------------------------------------------------------------------------------------------------- */

export type ShipmentFilter = ListParams & {
  status?: string;
  courierId?: string;
  branchId?: string;
  kind?: "Forward" | "Return" | "";
  orderId?: string;
  codOnly?: boolean;
  open?: boolean;
  from?: string;
  to?: string;
};

export function useShipments(filter: ShipmentFilter, options: { enabled?: boolean } = {}) {
  const { page, pageSize, ...rest } = filter;
  return useQuery({
    queryKey: [...COURIER_KEYS.shipments, "list", filter],
    placeholderData: keepPreviousData,
    enabled: options.enabled ?? true,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/shipments", {
          // The generated query type is a loose object; the API ignores empty values, so `clean` keeps the request small.
          params: { query: { Page: page, PageSize: pageSize, ...clean(rest) } as never },
        }),
      ),
  });
}

export function useShipment(id: string | undefined) {
  return useQuery({
    queryKey: [...COURIER_KEYS.shipments, "one", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/shipments/{id}", { params: { path: { id: id! } } })),
  });
}

/** The newest parcels of one order (forward and return), for the order screen. */
export function useOrderShipments(orderId: string | undefined, enabled = true) {
  return useShipments({ page: 1, pageSize: 10, orderId, sort: "bookedAt" }, { enabled: enabled && !!orderId });
}

export function useSuggestions(orderId: string | undefined, weightGrams?: number | null, enabled = true) {
  return useQuery({
    queryKey: [...COURIER_KEYS.all, "suggest", orderId, weightGrams ?? null],
    enabled: enabled && !!orderId,
    staleTime: 0,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/shipments/orders/{orderId}/suggest", {
          params: { path: { orderId: orderId! }, query: weightGrams ? { weightGrams } : {} },
        }),
      ),
  });
}

export function useBookShipment() {
  const invalidate = useInvalidateCouriers();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { orderId: string; body: BookRequest }) =>
      unwrap(await api.POST("/api/v1/shipments/orders/{orderId}/book", { params: { path: { orderId: vars.orderId } }, body: vars.body })),
    onSuccess: () => {
      invalidate();
      // The order moved on the delivery track too.
      return queryClient.invalidateQueries({ queryKey: ["orders"] });
    },
  });
}

export function useBulkBook() {
  const invalidate = useInvalidateCouriers();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { orderIds: string[]; courierId?: string | null }) =>
      unwrap(await api.POST("/api/v1/shipments/bulk-book", { body: { orderIds: vars.orderIds, courierId: vars.courierId ?? null } })),
    onSuccess: () => {
      invalidate();
      return queryClient.invalidateQueries({ queryKey: ["orders"] });
    },
  });
}

export function useCancelShipment() {
  const invalidate = useInvalidateCouriers();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { id: string; reason: string }) =>
      unwrap(await api.POST("/api/v1/shipments/{id}/cancel", { params: { path: { id: vars.id } }, body: { reason: vars.reason } })),
    onSuccess: () => {
      invalidate();
      return queryClient.invalidateQueries({ queryKey: ["orders"] });
    },
  });
}

export function useRebookShipment() {
  const invalidate = useInvalidateCouriers();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { id: string; body: Schemas["RebookShipmentRequest"] }) =>
      unwrap(await api.POST("/api/v1/shipments/{id}/rebook", { params: { path: { id: vars.id } }, body: vars.body })),
    onSuccess: () => {
      invalidate();
      return queryClient.invalidateQueries({ queryKey: ["orders"] });
    },
  });
}

export function useAdvanceShipment() {
  const invalidate = useInvalidateCouriers();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { id: string; status?: string | null; note?: string | null }) =>
      unwrap(
        await api.POST("/api/v1/shipments/{id}/advance", {
          params: { path: { id: vars.id } },
          body: { status: vars.status ?? null, note: vars.note ?? null },
        }),
      ),
    onSuccess: () => {
      invalidate();
      return queryClient.invalidateQueries({ queryKey: ["orders"] });
    },
  });
}

export function useLabel(id: string | undefined) {
  return useQuery({
    queryKey: [...COURIER_KEYS.shipments, "label", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/shipments/{id}/label", { params: { path: { id: id! } } })),
  });
}

/* ---- COD settlements ------------------------------------------------------------------------------------------------------- */

export type SettlementFilter = ListParams & { status?: string; courierId?: string; branchId?: string };

export function useSettlements(filter: SettlementFilter) {
  const { page, pageSize, ...rest } = filter;
  return useQuery({
    queryKey: [...COURIER_KEYS.settlements, "list", filter],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/couriers/cod-settlements", { params: { query: { Page: page, PageSize: pageSize, ...clean(rest) } as never } })),
  });
}

export function useSettlement(id: string | undefined) {
  return useQuery({
    queryKey: [...COURIER_KEYS.settlements, "one", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/couriers/cod-settlements/{id}", { params: { path: { id: id! } } })),
  });
}

export function usePendingCod(courierId: string | undefined, branchId?: string) {
  return useQuery({
    queryKey: [...COURIER_KEYS.settlements, "pending", courierId, branchId ?? null],
    enabled: !!courierId,
    staleTime: 0,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/couriers/cod-settlements/pending", { params: { query: { courierId: courierId!, ...(branchId ? { branchId } : {}) } as never } })),
  });
}

function useSettlementAction<TVars>(call: (vars: TVars) => Promise<unknown>) {
  const invalidate = useInvalidateCouriers();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: call,
    onSuccess: () => {
      invalidate();
      // Settling moves the cash to Settled on the payment records and orders.
      return Promise.all([queryClient.invalidateQueries({ queryKey: ["payments"] }), queryClient.invalidateQueries({ queryKey: ["orders"] })]);
    },
  });
}

export function useCreateSettlement() {
  return useSettlementAction(async (vars: { courierId: string; branchId?: string | null; shipmentIds?: string[] | null; note?: string | null }) =>
    unwrap(await api.POST("/api/v1/couriers/cod-settlements", { body: { courierId: vars.courierId, branchId: vars.branchId ?? null, shipmentIds: vars.shipmentIds ?? null, note: vars.note ?? null } })),
  );
}

export function useReconcileSettlement() {
  return useSettlementAction(async (vars: { id: string; lines: { shipmentId: string; received: number; note?: string | null }[] }) =>
    unwrap(await api.POST("/api/v1/couriers/cod-settlements/{id}/reconcile", { params: { path: { id: vars.id } }, body: { lines: vars.lines } as never })),
  );
}

export function useSettleSettlement() {
  return useSettlementAction(async (vars: { id: string; reference: string }) =>
    unwrap(await api.POST("/api/v1/couriers/cod-settlements/{id}/settle", { params: { path: { id: vars.id } }, body: { reference: vars.reference } })),
  );
}

export function useCancelSettlement() {
  return useSettlementAction(async (id: string) => unwrap(await api.POST("/api/v1/couriers/cod-settlements/{id}/cancel", { params: { path: { id } } })));
}
