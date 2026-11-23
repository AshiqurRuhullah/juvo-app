"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import type { KeyRequest, WebhookRequest } from "./logic";

/**
 * Typed access to API keys and webhooks (docs/public-api.md section 4, FR-AF05). Every call goes through the BFF.
 *
 * Secrets: the API answers a new key or signing secret exactly once (create, rotate). Those answers are returned to the caller of the
 * mutation and are never written to the query cache (`gcTime: 0` on the mutations and no `setQueryData`), so the one-time dialog holds
 * the only copy, in component state.
 */
export type ApiKey = Schemas["ApiKeyDto"];
export type ScopeInfo = Schemas["ScopeInfo"];
export type WebhookEndpoint = Schemas["WebhookEndpointDto"];
export type WebhookDelivery = Schemas["WebhookDeliveryDto"];

const KEYS = ["integrations", "api-keys"] as const;
const HOOKS = ["integrations", "webhooks"] as const;
type Query = Record<string, string | number>;

export function useApiKeys(query: Query) {
  return useQuery({
    queryKey: [...KEYS, "list", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/integrations/api-keys", { params: { query: query as never } })),
  });
}

export function useScopes() {
  return useQuery({ queryKey: [...KEYS, "scopes"], staleTime: 5 * 60_000, queryFn: async () => unwrap(await api.GET("/api/v1/integrations/api-keys/scopes")) });
}

function useInvalidate(root: readonly string[]) {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: root });
}

/** Create or update. Create answers `{ key, secret }`; the secret goes to the caller only. */
export function useSaveKey() {
  const invalidate = useInvalidate(KEYS);
  return useMutation({
    gcTime: 0,
    mutationFn: async (args: { id?: string; body: KeyRequest }) =>
      args.id
        ? { key: unwrap(await api.PUT("/api/v1/integrations/api-keys/{id}", { params: { path: { id: args.id } }, body: args.body })), secret: null as string | null }
        : unwrap(await api.POST("/api/v1/integrations/api-keys", { body: args.body })),
    onSuccess: invalidate,
  });
}

export function useRotateKey() {
  const invalidate = useInvalidate(KEYS);
  return useMutation({
    gcTime: 0,
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/integrations/api-keys/{id}/rotate", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

export function useSetKeyEnabled() {
  const invalidate = useInvalidate(KEYS);
  return useMutation({
    mutationFn: async (args: { id: string; enabled: boolean }) =>
      unwrap(await api.POST(args.enabled ? "/api/v1/integrations/api-keys/{id}/enable" : "/api/v1/integrations/api-keys/{id}/disable", { params: { path: { id: args.id } } })),
    onSuccess: invalidate,
  });
}

export function useDeleteKey() {
  const invalidate = useInvalidate(KEYS);
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/integrations/api-keys/{id}", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

// ---- Webhooks ----------------------------------------------------------------------------------------------------------------------

export function useWebhooks(query: Query) {
  return useQuery({
    queryKey: [...HOOKS, "list", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/integrations/webhooks", { params: { query: query as never } })),
  });
}

/** Every endpoint (the API allows 20 at most) for the endpoint filter of the delivery log. */
export function useAllWebhooks() {
  return useQuery({
    queryKey: [...HOOKS, "all"],
    queryFn: async () => unwrap(await api.GET("/api/v1/integrations/webhooks", { params: { query: { Page: 1, PageSize: 100 } as never } })).items,
  });
}

export function useDeliveries(query: Query, options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: [...HOOKS, "deliveries", query],
    placeholderData: keepPreviousData,
    enabled: options.enabled ?? true,
    queryFn: async () => unwrap(await api.GET("/api/v1/integrations/webhooks/deliveries", { params: { query: query as never } })),
  });
}

export function useSaveWebhook() {
  const invalidate = useInvalidate(HOOKS);
  return useMutation({
    gcTime: 0,
    mutationFn: async (args: { id?: string; body: WebhookRequest }) =>
      args.id
        ? { endpoint: unwrap(await api.PUT("/api/v1/integrations/webhooks/{id}", { params: { path: { id: args.id } }, body: args.body })), secret: null as string | null }
        : unwrap(await api.POST("/api/v1/integrations/webhooks", { body: args.body })),
    onSuccess: invalidate,
  });
}

export function useRotateWebhookSecret() {
  const invalidate = useInvalidate(HOOKS);
  return useMutation({
    gcTime: 0,
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/integrations/webhooks/{id}/rotate-secret", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

export function useSetWebhookEnabled() {
  const invalidate = useInvalidate(HOOKS);
  return useMutation({
    mutationFn: async (args: { id: string; enabled: boolean }) =>
      unwrap(await api.POST(args.enabled ? "/api/v1/integrations/webhooks/{id}/enable" : "/api/v1/integrations/webhooks/{id}/disable", { params: { path: { id: args.id } } })),
    onSuccess: invalidate,
  });
}

export function useDeleteWebhook() {
  const invalidate = useInvalidate(HOOKS);
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.DELETE("/api/v1/integrations/webhooks/{id}", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

/** The test ping answers the delivery with its outcome (a failed ping is a normal answer, not an error). */
export function useTestWebhook() {
  const invalidate = useInvalidate(HOOKS);
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/integrations/webhooks/{id}/test", { params: { path: { id } } })),
    onSettled: invalidate,
  });
}

export function useRedeliver() {
  const invalidate = useInvalidate(HOOKS);
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/integrations/webhooks/deliveries/{id}/redeliver", { params: { path: { id } } })),
    onSettled: invalidate,
  });
}
