"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";
import { dhakaDayBound, type PromotionType, type SavePromotion } from "./logic";

/** Typed access to the promotion endpoints (module Z, docs/promotions-api.md). Every call goes through the BFF. */
export type PromotionListItem = Schemas["PromotionListItemDto"];
export type PromotionDetail = Schemas["PromotionDetailDto"];
export type PromotionReport = Schemas["PromotionReportDto"];
export type PreviewResult = Schemas["PreviewResultDto"];
export type PreviewRequest = Schemas["PreviewRequest"];

const KEY = ["promotions"] as const;

const clean = <T extends Record<string, unknown>>(value: T) => Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null)) as Partial<T>;

export type ListArgs = { page: number; pageSize: number; sort?: string; search?: string; type?: string; status?: string };

export function usePromotions(args: ListArgs) {
  return useQuery({
    queryKey: [...KEY, "list", args],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/promotions", {
          params: { query: clean({ Page: args.page, PageSize: args.pageSize, Sort: args.sort, Search: args.search, type: args.type as PromotionType | undefined, status: args.status as Schemas["PromotionStatus"] | undefined }) as never },
        }),
      ),
  });
}

export function usePromotion(id: string) {
  return useQuery({
    queryKey: [...KEY, "detail", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/promotions/{id}", { params: { path: { id } } })),
  });
}

function useAfterChange() {
  const queryClient = useQueryClient();
  return (detail?: PromotionDetail) => {
    if (detail) queryClient.setQueryData([...KEY, "detail", detail.id], detail);
    return Promise.all([queryClient.invalidateQueries({ queryKey: [...KEY, "list"] }), queryClient.invalidateQueries({ queryKey: [...KEY, "report"] })]);
  };
}

export function useCreatePromotion() {
  const after = useAfterChange();
  return useMutation({ mutationFn: async (body: SavePromotion) => unwrap(await api.POST("/api/v1/promotions", { body })), onSuccess: (d) => after(d) });
}

export function useUpdatePromotion(id: string) {
  const after = useAfterChange();
  return useMutation({ mutationFn: async (body: SavePromotion) => unwrap(await api.PUT("/api/v1/promotions/{id}", { params: { path: { id } }, body })), onSuccess: (d) => after(d) });
}

export type PromotionAction = "activate" | "deactivate" | "archive" | "duplicate";

export function usePromotionAction(id: string) {
  const after = useAfterChange();
  return useMutation({
    mutationFn: async (action: PromotionAction) => {
      const params = { params: { path: { id } } };
      if (action === "activate") return unwrap(await api.POST("/api/v1/promotions/{id}/activate", params));
      if (action === "deactivate") return unwrap(await api.POST("/api/v1/promotions/{id}/deactivate", params));
      if (action === "archive") return unwrap(await api.POST("/api/v1/promotions/{id}/archive", params));
      return unwrap(await api.POST("/api/v1/promotions/{id}/duplicate", params));
    },
    onSuccess: (d) => after(d),
  });
}

/** Multipart upload through the BFF (the typed client is JSON only). Throws an ApiError with the API's problem. */
export async function uploadPromotionImage(id: string, kind: "hero" | "banner", file: File): Promise<PromotionDetail> {
  const form = new FormData();
  form.append("file", file);
  const response = await fetch(`/api/bff/api/v1/promotions/${id}/images/${kind}`, { method: "POST", body: form, credentials: "same-origin" });
  const text = await response.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;
  if (!response.ok) throw new ApiError(toProblem(parsed, response.status));
  return parsed as PromotionDetail;
}

export function useUploadPromotionImage(id: string) {
  const after = useAfterChange();
  return useMutation({ mutationFn: (vars: { kind: "hero" | "banner"; file: File }) => uploadPromotionImage(id, vars.kind, vars.file), onSuccess: (d) => after(d) });
}

export function usePreview() {
  return useMutation({ mutationFn: async (body: PreviewRequest) => unwrap(await api.POST("/api/v1/promotions/preview", { body })) });
}

/* ---- Reports -------------------------------------------------------------------------------------------------------------- */

export type ReportArgs = { page: number; pageSize: number; sort?: string; from?: string; to?: string; type?: string };

export function useCampaignReport(args: ReportArgs) {
  return useQuery({
    queryKey: [...KEY, "report", "page", args],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/promotions/report", {
          params: { query: clean({ Page: args.page, PageSize: args.pageSize, Sort: args.sort, from: dhakaDayBound(args.from, false), to: dhakaDayBound(args.to, true), type: args.type as PromotionType | undefined }) as never },
        }),
      ),
  });
}

/** Every promotion's figures for the strip on the list: all pages of the report (100 per page), up to 20 pages. */
export function useCampaignTotals() {
  return useQuery({
    queryKey: [...KEY, "report", "all"],
    queryFn: async () => {
      const rows: PromotionReport[] = [];
      let total = 0;
      for (let page = 1; page <= 20; page++) {
        const answer = unwrap(await api.GET("/api/v1/promotions/report", { params: { query: { Page: page, PageSize: 100 } as never } }));
        rows.push(...answer.items);
        total = Number(answer.total);
        if (rows.length >= total || answer.items.length === 0) break;
      }
      return { rows, total };
    },
  });
}

export function usePromotionReport(id: string, from?: string, to?: string) {
  return useQuery({
    queryKey: [...KEY, "report", "one", id, from, to],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/promotions/{id}/report", { params: { path: { id }, query: clean({ from: dhakaDayBound(from, false), to: dhakaDayBound(to, true) }) as never } })),
  });
}
