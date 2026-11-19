"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import type { BrandLike, BrandRequest, PageForm, Platform } from "./logic";

/** Typed access to the staff content endpoints (docs/content-api.md). Every call goes through the BFF. */
export type ContentPage = Schemas["PageDto"];
export type SocialLink = Schemas["SocialLinkDto"];
export type TrustBadge = Schemas["TrustBadgeDto"];
export type SaveBadgeBody = Schemas["SaveTrustBadgeRequest"];
/**
 * The brand profile answer. The generated client names the content brand `BrandDto`, the same name as the catalogue brand, so its
 * type for this endpoint is wrong (contract gap, docs/content-ui-notes.md). The real shape is the save body plus `updatedAt`.
 */
export type BrandProfile = BrandLike & { updatedAt: string };

const KEY = ["content"] as const;

export const usePages = () => useQuery({ queryKey: [...KEY, "pages"], queryFn: async () => unwrap(await api.GET("/api/v1/content/pages")) });

export const usePage = (key: string) =>
  useQuery({
    queryKey: [...KEY, "page", key],
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/content/pages/{key}", { params: { path: { key } } })),
  });

function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: KEY });
}

export function useSavePage(key: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (form: PageForm) => unwrap(await api.PUT("/api/v1/content/pages/{key}", { params: { path: { key } }, body: form })),
    onSuccess: invalidate,
  });
}

export function usePublishPage(key: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (publish: boolean) =>
      publish
        ? unwrap(await api.POST("/api/v1/content/pages/{key}/publish", { params: { path: { key } } }))
        : unwrap(await api.POST("/api/v1/content/pages/{key}/unpublish", { params: { path: { key } } })),
    onSuccess: invalidate,
  });
}

export const useBrand = () =>
  useQuery({
    queryKey: [...KEY, "brand"],
    queryFn: async () => unwrap(await api.GET("/api/v1/content/brand")) as unknown as BrandProfile,
  });

export function useSaveBrand() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (body: BrandRequest) => unwrap(await api.PUT("/api/v1/content/brand", { body })) as unknown as BrandProfile,
    onSuccess: invalidate,
  });
}

export const useSocial = () => useQuery({ queryKey: [...KEY, "social"], queryFn: async () => unwrap(await api.GET("/api/v1/content/social")) });

export function useSaveSocial() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (args: { platform: Platform; url: string; isEnabled: boolean; sortOrder: number }) =>
      unwrap(await api.PUT("/api/v1/content/social/{platform}", { params: { path: { platform: args.platform } }, body: { url: args.url, isEnabled: args.isEnabled, sortOrder: args.sortOrder } })),
    onSuccess: invalidate,
  });
}

export function useDeleteSocial() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (platform: Platform) => unwrap(await api.DELETE("/api/v1/content/social/{platform}", { params: { path: { platform } } })),
    onSuccess: invalidate,
  });
}

export const useBadges = () => useQuery({ queryKey: [...KEY, "badges"], queryFn: async () => unwrap(await api.GET("/api/v1/content/badges")) });

export function useSaveBadge(key: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (body: SaveBadgeBody) => unwrap(await api.PUT("/api/v1/content/badges/{key}", { params: { path: { key } }, body })),
    onSuccess: invalidate,
  });
}
