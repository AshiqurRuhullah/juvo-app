"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";

/** Typed access to the delivery endpoints (docs/storefront-api.md): staff zones, rates, areas and countries, the public geography and the public quote. */
export type Zone = Schemas["DeliveryZoneDto"];
export type Country = Schemas["CountryDto"];
export type Division = Schemas["DivisionDto"];
export type District = Schemas["DistrictDto"];
export type Upazila = Schemas["UpazilaDto"];
export type Quote = Schemas["DeliveryQuoteDto"];

const KEY = ["delivery"] as const;

export const useZones = () =>
  useQuery({ queryKey: [...KEY, "zones"], queryFn: async () => unwrap(await api.GET("/api/v1/delivery/zones")) });

export const useZone = (id: string) =>
  useQuery({
    queryKey: [...KEY, "zone", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/delivery/zones/{id}", { params: { path: { id } } })),
  });

function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: KEY });
}

export function useCreateZone() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (body: Schemas["CreateZoneRequest"]) => unwrap(await api.POST("/api/v1/delivery/zones", { body })),
    onSuccess: invalidate,
  });
}

export function useUpdateZone(id: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (body: Schemas["UpdateZoneRequest"]) => unwrap(await api.PUT("/api/v1/delivery/zones/{id}", { params: { path: { id } }, body })),
    onSuccess: invalidate,
  });
}

export function useSaveRates(id: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (body: Schemas["SaveRatesRequest"]) => unwrap(await api.PUT("/api/v1/delivery/zones/{id}/rates", { params: { path: { id } }, body })),
    onSuccess: invalidate,
  });
}

export function useSaveAreas(id: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (areas: Schemas["AreaDto"][]) => unwrap(await api.PUT("/api/v1/delivery/zones/{id}/areas", { params: { path: { id } }, body: { areas } })),
    onSuccess: invalidate,
  });
}

export function useDeleteZone() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/delivery/zones/{id}", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

// ---- countries -----------------------------------------------------------------------------------------------

export const useCountries = (enabled: boolean) =>
  useQuery({ queryKey: [...KEY, "countries"], enabled, retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/delivery/countries")) });

export function useSaveCountry() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (args: { id?: string; body: Schemas["SaveCountryRequest"] }) =>
      args.id ? unwrap(await api.PUT("/api/v1/delivery/countries/{id}", { params: { path: { id: args.id } }, body: args.body })) : unwrap(await api.POST("/api/v1/delivery/countries", { body: args.body })),
    onSuccess: invalidate,
  });
}

// ---- geography (public, never changes during a visit) --------------------------------------------------------------

export const useDivisions = () => useQuery({ queryKey: [...KEY, "geo", "divisions"], staleTime: Infinity, queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/divisions")) });
export const useDistricts = () => useQuery({ queryKey: [...KEY, "geo", "districts"], staleTime: Infinity, queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/districts")) });

/** Upazilas of one district, read when the district is opened. */
export const useUpazilas = (districtId: string | null) =>
  useQuery({
    queryKey: [...KEY, "geo", "upazilas", districtId],
    enabled: !!districtId,
    staleTime: 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/districts/{districtId}/upazilas", { params: { path: { districtId: districtId! } } })),
  });

export function useImportUpazilas() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: Schemas["ImportUpazilasRequest"]) => unwrap(await api.POST("/api/v1/delivery/upazilas/import", { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "geo", "upazilas"] }),
  });
}

/** The same call the storefront makes (docs: "the charge shown is the charge billed"), so staff see what a customer would see. */
export function useQuoteTry() {
  return useMutation({
    mutationFn: async (body: Schemas["DeliveryQuoteRequest"]) => unwrap(await api.POST("/api/v1/store/delivery/quote", { body })),
  });
}
