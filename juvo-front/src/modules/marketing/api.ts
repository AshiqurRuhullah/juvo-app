"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { campaignToRequest, type CampaignForm, type SegmentDefinition } from "./logic";

/** Typed access to the marketing endpoints (module AC, docs/marketing-api.md). Every call goes through the BFF. */
export type Segment = Schemas["SegmentDto"];
export type SegmentCount = Schemas["SegmentCountDto"];
export type SegmentMember = Schemas["SegmentMemberDto"];
export type Campaign = Schemas["CampaignDto"];
export type CampaignListItem = Schemas["CampaignListItemDto"];
export type CampaignPreview = Schemas["CampaignPreviewDto"];
export type CampaignRecipient = Schemas["CampaignRecipientDto"];
export type CampaignResults = Schemas["CampaignResultsDto"];
export type Automation = Schemas["AutomationDto"];
export type AutomationDryRun = Schemas["AutomationDryRunDto"];
export type AutomationResults = Schemas["AutomationResultsDto"];
export type MarketingSettings = Schemas["MarketingSettingsDto"];
export type UpdateAutomation = Schemas["UpdateAutomationRequest"];
export type UpdateSettings = Schemas["UpdateMarketingSettingsRequest"];
export type NotifyMeResult = Schemas["NotifyMeDto"];
export type UnsubscribeResult = Schemas["UnsubscribeDto"];

const KEY = ["marketing"] as const;

const clean = <T extends Record<string, unknown>>(value: T) => Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null)) as Partial<T>;

/* ---- Segments --------------------------------------------------------------------------------------------------------------- */

export function useSegments(args: { page: number; pageSize: number; search?: string }) {
  return useQuery({
    queryKey: [...KEY, "segments", "list", args],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/marketing/segments", { params: { query: clean({ Page: args.page, PageSize: args.pageSize, Search: args.search }) } })),
  });
}

/** Every segment (for the campaign form's picker): the first 100, built-in ones first. */
export const useSegmentOptions = (enabled = true) =>
  useQuery({
    queryKey: [...KEY, "segments", "options"],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/marketing/segments", { params: { query: { Page: 1, PageSize: 100 } } })),
  });

export const useSegment = (id: string | null | undefined) =>
  useQuery({
    queryKey: [...KEY, "segments", "detail", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/marketing/segments/{id}", { params: { path: { id: id! } } })),
  });

/** The live size of a saved segment. Each call is one set-based query on the server. */
export const useSegmentCount = (id: string | null | undefined, enabled = true) =>
  useQuery({
    queryKey: [...KEY, "segments", "count", id],
    enabled: !!id && enabled,
    staleTime: 15_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/marketing/segments/{id}/count", { params: { path: { id: id! } } })),
  });

/** The size of unsaved rules, recomputed (after a short pause) while the rules are edited. `definition` null = not yet valid, no call. */
export const usePreviewCount = (definition: SegmentDefinition | null) =>
  useQuery({
    queryKey: [...KEY, "segments", "preview-count", definition],
    enabled: definition !== null,
    retry: false,
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.POST("/api/v1/marketing/segments/preview-count", { body: { definition } })),
  });

export function useSegmentMembers(id: string | null | undefined, args: { page: number; pageSize: number; sort?: string; search?: string }) {
  return useQuery({
    queryKey: [...KEY, "segments", "members", id, args],
    enabled: !!id,
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/marketing/segments/{id}/members", { params: { path: { id: id! }, query: clean({ Page: args.page, PageSize: args.pageSize, Sort: args.sort, Search: args.search }) } })),
  });
}

export type SaveSegment = { name: string; description: string | null; definition: SegmentDefinition };

export function useSaveSegment() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (args: { id?: string; body: SaveSegment }) =>
      args.id
        ? unwrap(await api.PUT("/api/v1/marketing/segments/{id}", { params: { path: { id: args.id } }, body: args.body }))
        : unwrap(await api.POST("/api/v1/marketing/segments", { body: args.body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "segments"] }),
  });
}

export function useCopySegment() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/marketing/segments/{id}/copy", { params: { path: { id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "segments"] }),
  });
}

export function useDeleteSegment() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/marketing/segments/{id}", { params: { path: { id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "segments"] }),
  });
}

/* ---- Campaigns -------------------------------------------------------------------------------------------------------------- */

export type CampaignFilters = { status?: string; channel?: string; segmentId?: string };

export function useCampaigns(args: { filters: CampaignFilters; page: number; pageSize: number; search?: string; sort?: string }) {
  return useQuery({
    queryKey: [...KEY, "campaigns", "list", args],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/marketing/campaigns", {
          params: {
            query: clean({
              Page: args.page,
              PageSize: args.pageSize,
              Sort: args.sort,
              Search: args.search,
              status: args.filters.status as Campaign["status"] | undefined,
              channel: args.filters.channel as Campaign["channel"] | undefined,
              segmentId: args.filters.segmentId,
            }),
          },
        }),
      ),
  });
}

export const useCampaign = (id: string | null | undefined) =>
  useQuery({
    queryKey: [...KEY, "campaigns", "detail", id],
    enabled: !!id,
    retry: false,
    // A campaign that is sending changes under our feet: look again every few seconds until it is done.
    refetchInterval: (query) => (query.state.data?.status === "Sending" ? 5000 : false),
    queryFn: async () => unwrap(await api.GET("/api/v1/marketing/campaigns/{id}", { params: { path: { id: id! } } })),
  });

function useAfterCampaignChange() {
  const queryClient = useQueryClient();
  return (campaign?: Campaign) => {
    if (campaign) queryClient.setQueryData([...KEY, "campaigns", "detail", campaign.id], campaign);
    return Promise.all([queryClient.invalidateQueries({ queryKey: [...KEY, "campaigns", "list"] }), campaign ? queryClient.invalidateQueries({ queryKey: [...KEY, "campaigns", "results", campaign.id] }) : undefined]);
  };
}

export function useSaveCampaign() {
  const after = useAfterCampaignChange();
  return useMutation({
    mutationFn: async (args: { id?: string; form: CampaignForm }) => {
      const body = campaignToRequest(args.form);
      return args.id
        ? unwrap(await api.PUT("/api/v1/marketing/campaigns/{id}", { params: { path: { id: args.id } }, body }))
        : unwrap(await api.POST("/api/v1/marketing/campaigns", { body }));
    },
    onSuccess: (campaign) => after(campaign),
  });
}

export function useDeleteCampaign() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/marketing/campaigns/{id}", { params: { path: { id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "campaigns"] }),
  });
}

export function useCampaignPreview() {
  return useMutation({ mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/marketing/campaigns/{id}/preview", { params: { path: { id } } })) });
}

export function useScheduleCampaign() {
  const after = useAfterCampaignChange();
  return useMutation({
    mutationFn: async (args: { id: string; scheduledAt: string }) => unwrap(await api.POST("/api/v1/marketing/campaigns/{id}/schedule", { params: { path: { id: args.id } }, body: { scheduledAt: args.scheduledAt } })),
    onSuccess: (campaign) => after(campaign),
  });
}

export function useSendCampaign() {
  const after = useAfterCampaignChange();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/marketing/campaigns/{id}/send", { params: { path: { id } } })),
    onSuccess: (campaign) => after(campaign),
  });
}

export function useCancelCampaign() {
  const after = useAfterCampaignChange();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/marketing/campaigns/{id}/cancel", { params: { path: { id } } })),
    onSuccess: (campaign) => after(campaign),
  });
}

export function useSendTest() {
  return useMutation({
    mutationFn: async (args: { id: string; language: "en" | "bn" }) => unwrap(await api.POST("/api/v1/marketing/campaigns/{id}/test", { params: { path: { id: args.id } }, body: { language: args.language } })),
  });
}

export function useCampaignRecipients(id: string, args: { page: number; pageSize: number; status?: string; reason?: string; search?: string }, enabled = true) {
  return useQuery({
    queryKey: [...KEY, "campaigns", "recipients", id, args],
    enabled,
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/marketing/campaigns/{id}/recipients", {
          params: { path: { id }, query: clean({ Page: args.page, PageSize: args.pageSize, Search: args.search, status: args.status as CampaignRecipient["status"] | undefined, reason: args.reason }) },
        }),
      ),
  });
}

export const useCampaignResults = (id: string, enabled = true, live = false) =>
  useQuery({
    queryKey: [...KEY, "campaigns", "results", id],
    enabled,
    // Keep looking while the campaign is sending or messages are still waiting or on their way (the worker delivers after the send).
    refetchInterval: (query) => {
      const d = query.state.data;
      return live || (d && (Number(d.inFlight) > 0 || Number(d.pending) > 0)) ? 5000 : false;
    },
    queryFn: async () => unwrap(await api.GET("/api/v1/marketing/campaigns/{id}/results", { params: { path: { id } } })),
  });

/* ---- Automations ------------------------------------------------------------------------------------------------------------ */

export const useAutomations = () => useQuery({ queryKey: [...KEY, "automations"], queryFn: async () => unwrap(await api.GET("/api/v1/marketing/automations")) });

export function useUpdateAutomation() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (args: { kind: Automation["kind"]; body: UpdateAutomation }) => unwrap(await api.PUT("/api/v1/marketing/automations/{kind}", { params: { path: { kind: args.kind } }, body: args.body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "automations"] }),
  });
}

/** What every automation would do right now. Nothing is written. */
export function useDryRun() {
  return useMutation({ mutationFn: async () => unwrap(await api.POST("/api/v1/marketing/automations/dry-run")) });
}

export const useAutomationResults = (kind: Automation["kind"], enabled = true) =>
  useQuery({
    queryKey: [...KEY, "automations", "results", kind],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/marketing/automations/{kind}/results", { params: { path: { kind } } })),
  });

/* ---- Settings --------------------------------------------------------------------------------------------------------------- */

export const useMarketingSettings = () => useQuery({ queryKey: [...KEY, "settings"], queryFn: async () => unwrap(await api.GET("/api/v1/marketing/settings")) });

export function useUpdateMarketingSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: UpdateSettings) => unwrap(await api.PUT("/api/v1/marketing/settings", { body })),
    onSuccess: (settings) => {
      queryClient.setQueryData([...KEY, "settings"], settings);
      return queryClient.invalidateQueries({ queryKey: [...KEY, "automations"] });
    },
  });
}

/* ---- Public ----------------------------------------------------------------------------------------------------------------- */

export function useNotifyMe(slug: string) {
  return useMutation({
    mutationFn: async (body: { variantId: string | null; mobile: string | null }) =>
      unwrap(await api.POST("/api/v1/store/products/{slug}/notify-me", { params: { path: { slug } }, body: { variantId: body.variantId, mobile: body.mobile, email: null } })),
  });
}

export function useUnsubscribe() {
  return useMutation({
    mutationFn: async (token: string) => unwrap(await api.POST("/api/v1/store/unsubscribe", { params: { query: { token } } })),
  });
}

/* ---- Pickers ---------------------------------------------------------------------------------------------------------------- */

/** All districts (the location rule can name divisions or districts). The geography never changes while the page is open. */
export const useAllDistricts = () => useQuery({ queryKey: [...KEY, "geo", "districts"], staleTime: Infinity, queryFn: async () => unwrap(await api.GET("/api/v1/store/geo/districts")) });

/** A product search for the wishlist rule (needs `catalog.product.view`). */
export const useProductSearch = (search: string, enabled: boolean) =>
  useQuery({
    queryKey: [...KEY, "product-search", search],
    enabled: enabled && search.trim().length >= 2,
    queryFn: async () => unwrap(await api.GET("/api/v1/catalog/products", { params: { query: { Page: 1, PageSize: 8, Search: search.trim() } } })),
  });
