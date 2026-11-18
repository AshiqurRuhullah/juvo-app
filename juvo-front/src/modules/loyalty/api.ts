"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { newKey } from "@/modules/orders/status";
import type { AdjustDraft, GiftCardDraft, SettingsForm } from "./logic";
import { toAdjustBody, toGiftCardBody, toSettingsPatch, type LoyaltySettings } from "./logic";

/** Typed access to the rewards endpoints (module AB, docs/loyalty-api.md). Every call goes through the BFF. */
export type RewardsSummary = Schemas["RewardsSummaryDto"];
export type PointsEntry = Schemas["PointsEntryDto"];
export type CreditEntry = Schemas["CreditEntryDto"];
export type MyReferral = Schemas["MyReferralDto"];
export type GiftCard = Schemas["GiftCardDto"];
export type GiftCardDetail = Schemas["GiftCardDetailDto"];
export type GiftCardCreated = Schemas["GiftCardCreatedDto"];
export type ReferralListItem = Schemas["ReferralListItemDto"];
export type CustomerRewards = Schemas["CustomerRewardsDto"];
export type CustomerListItem = Schemas["CustomerListItemDto"];

const KEY = ["loyalty"] as const;

/** True when the API answered "the rewards feature is off" (409 LOYALTY.FEATURE_OFF). */
export const isFeatureOff = (error: unknown): boolean => error instanceof ApiError && error.problem.code === "LOYALTY.FEATURE_OFF";

/* ---- Customer ------------------------------------------------------------------------------------------------------------- */

/**
 * My balances and the rules. While the feature is off the API answers 409; this hook then settles to `data: null` (not an error), so
 * callers can simply hide rewards. Other failures stay errors.
 */
export function useMyRewards(enabled = true) {
  return useQuery({
    queryKey: [...KEY, "me"],
    enabled,
    retry: false,
    staleTime: 60_000,
    queryFn: async (): Promise<RewardsSummary | null> => {
      try {
        const summary = unwrap(await api.GET("/api/v1/me/rewards"));
        return summary.enabled ? summary : null;
      } catch (failure) {
        if (isFeatureOff(failure)) return null;
        throw failure;
      }
    },
  });
}

export const useMyPoints = (page: number, pageSize: number) =>
  useQuery({
    queryKey: [...KEY, "me", "points", page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/rewards/points", { params: { query: { Page: page, PageSize: pageSize } } })),
  });

export const useMyCredit = (page: number, pageSize: number) =>
  useQuery({
    queryKey: [...KEY, "me", "credit", page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/rewards/credit", { params: { query: { Page: page, PageSize: pageSize } } })),
  });

/** How much I can use on an order of this total (balances, minimum and the cap applied). */
export const useUsable = (orderTotal: number, enabled: boolean) =>
  useQuery({
    queryKey: [...KEY, "me", "usable", orderTotal],
    enabled: enabled && orderTotal > 0,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/rewards/usable", { params: { query: { orderTotal } } })),
  });

export const useMyReferral = (enabled = true) =>
  useQuery({ queryKey: [...KEY, "me", "referral"], enabled, retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/me/rewards/referral")) });

function useAfterMyChange() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: [...KEY, "me"] });
}

export function useRedeemGiftCard() {
  const after = useAfterMyChange();
  return useMutation({
    mutationFn: async (code: string) => unwrap(await api.POST("/api/v1/me/rewards/gift-cards/redeem", { body: { code } })),
    onSuccess: after,
  });
}

export function useApplyReferral() {
  const after = useAfterMyChange();
  return useMutation({
    mutationFn: async (code: string) => unwrap(await api.POST("/api/v1/me/rewards/referral/apply", { body: { code } })),
    onSuccess: after,
  });
}

/* ---- Staff ---------------------------------------------------------------------------------------------------------------- */

export const useLoyaltySettings = () => useQuery({ queryKey: [...KEY, "settings"], queryFn: async () => unwrap(await api.GET("/api/v1/loyalty/settings")) });

export function useSaveSettings() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { original: LoyaltySettings; form: SettingsForm }) =>
      unwrap(await api.PUT("/api/v1/loyalty/settings", { body: toSettingsPatch(vars.original, vars.form) })),
    onSuccess: (saved) => {
      queryClient.setQueryData([...KEY, "settings"], saved);
      // Customer-side views read the flag and the rules from here.
      return queryClient.invalidateQueries({ queryKey: [...KEY, "me"] });
    },
  });
}

export const useGiftCards = (args: { page: number; pageSize: number; status?: string }) =>
  useQuery({
    queryKey: [...KEY, "cards", args],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/loyalty/gift-cards", { params: { query: { Page: args.page, PageSize: args.pageSize, status: args.status } } })),
  });

export const useGiftCardDetail = (id: string | null) =>
  useQuery({
    queryKey: [...KEY, "cards", "detail", id],
    enabled: !!id,
    queryFn: async () => unwrap(await api.GET("/api/v1/loyalty/gift-cards/{id}", { params: { path: { id: id! } } })),
  });

export function useCreateGiftCard() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (draft: GiftCardDraft) => unwrap(await api.POST("/api/v1/loyalty/gift-cards", { body: toGiftCardBody(draft) })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "cards"] }),
  });
}

export function useDeactivateGiftCard() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/loyalty/gift-cards/{id}/deactivate", { params: { path: { id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "cards"] }),
  });
}

export const useReferrals = (args: { page: number; pageSize: number; status?: string }) =>
  useQuery({
    queryKey: [...KEY, "referrals", args],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/loyalty/referrals", { params: { query: { Page: args.page, PageSize: args.pageSize, status: (args.status || undefined) as Schemas["ReferralStatus"] | undefined } } })),
  });

/** Customer search by mobile or name (needs customers.customer.view; the lookup tab explains when it is missing). */
export const useCustomerSearch = (search: string, enabled: boolean) =>
  useQuery({
    queryKey: [...KEY, "customer-search", search],
    enabled: enabled && search.trim().length >= 3,
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/customers", { params: { query: { Search: search.trim(), PageSize: 8 } } })),
  });

export const useCustomerRewards = (customerId: string | null) =>
  useQuery({
    queryKey: [...KEY, "customer", customerId],
    enabled: !!customerId,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/loyalty/customers/{customerId}", { params: { path: { customerId: customerId! } } })),
  });

export const useCustomerPoints = (customerId: string | null, page: number, pageSize: number) =>
  useQuery({
    queryKey: [...KEY, "customer", customerId, "points", page, pageSize],
    enabled: !!customerId,
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/loyalty/customers/{customerId}/points", { params: { path: { customerId: customerId! }, query: { Page: page, PageSize: pageSize } } })),
  });

export const useCustomerCredit = (customerId: string | null, page: number, pageSize: number) =>
  useQuery({
    queryKey: [...KEY, "customer", customerId, "credit", page, pageSize],
    enabled: !!customerId,
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/loyalty/customers/{customerId}/credit", { params: { path: { customerId: customerId! }, query: { Page: page, PageSize: pageSize } } })),
  });

/** A manual adjustment. One Idempotency-Key belongs to one dialog opening: pass the same `key` again to retry safely. */
export function useAdjustRewards() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { customerId: string; draft: AdjustDraft; key?: string }) =>
      unwrap(
        await api.POST("/api/v1/loyalty/customers/{customerId}/adjust", {
          params: { path: { customerId: vars.customerId } },
          headers: { "Idempotency-Key": vars.key ?? newKey() },
          body: toAdjustBody(vars.draft),
        }),
      ),
    onSuccess: (_result, vars) => queryClient.invalidateQueries({ queryKey: [...KEY, "customer", vars.customerId] }),
  });
}
