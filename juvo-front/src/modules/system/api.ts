"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { REFRESH_MS } from "./logic";

/** Typed access to background jobs, queues and integration health (docs/system-api.md, FR-AF02 and FR-AF04). Every call goes through the BFF. */
export type JobRun = Schemas["JobRunDto"];
export type JobSummary = Schemas["JobSummaryDto"];
export type OutboxMessage = Schemas["OutboxMessageDto"];
export type OutboxSummary = Schemas["OutboxSummaryDto"];
export type FailedSend = Schemas["FailedNotificationDto"];
export type RequeueResultDto = Schemas["RequeueResultDto"];
export type IntegrationHealth = Schemas["IntegrationHealthDto"];
export type IntegrationTestResult = Schemas["IntegrationTestResultDto"];
export type SystemStatus = Schemas["SystemStatusDto"];

const KEY = ["system"] as const;
type Query = Record<string, string | number>;

export function useJobSummary() {
  return useQuery({ queryKey: [...KEY, "summary"], queryFn: async () => unwrap(await api.GET("/api/v1/system/jobs/summary")) });
}

export function useJobRuns(query: Query) {
  return useQuery({
    queryKey: [...KEY, "runs", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/jobs", { params: { query: query as never } })),
  });
}

export function useOutboxSummary() {
  return useQuery({ queryKey: [...KEY, "outbox-summary"], queryFn: async () => unwrap(await api.GET("/api/v1/system/jobs/outbox/summary")) });
}

export function useOutbox(query: Query) {
  return useQuery({
    queryKey: [...KEY, "outbox", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/jobs/outbox", { params: { query: query as never } })),
  });
}

export function useFailedSends(query: Query) {
  return useQuery({
    queryKey: [...KEY, "failed", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/jobs/notifications/failed", { params: { query: query as never } })),
  });
}

/** After a requeue or retry the lists, the counters and the summary all change. */
function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: KEY });
}

export function useRequeueOutbox() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (ids: string[]) =>
      ids.length === 1
        ? unwrap(await api.POST("/api/v1/system/jobs/outbox/{id}/requeue", { params: { path: { id: ids[0]! } } }))
        : unwrap(await api.POST("/api/v1/system/jobs/outbox/requeue", { body: { ids } })),
    onSettled: invalidate,
  });
}

export function useRetryFailedSends() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (ids: string[]) =>
      ids.length === 1
        ? unwrap(await api.POST("/api/v1/system/jobs/notifications/{id}/retry", { params: { path: { id: ids[0]! } } }))
        : unwrap(await api.POST("/api/v1/system/jobs/notifications/retry", { body: { ids } })),
    onSettled: invalidate,
  });
}

/** Health and status refresh every 30 seconds; TanStack Query pauses the timer while the tab is hidden (refetchIntervalInBackground is off). */
export function useIntegrationHealth() {
  return useQuery({
    queryKey: [...KEY, "health"],
    refetchInterval: REFRESH_MS,
    refetchIntervalInBackground: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/integrations/health")),
  });
}

export function useSystemStatus() {
  return useQuery({
    queryKey: [...KEY, "status"],
    refetchInterval: REFRESH_MS,
    refetchIntervalInBackground: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/status")),
  });
}

export function useTestIntegration() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (key: string) => unwrap(await api.POST("/api/v1/system/integrations/{key}/test", { params: { path: { key } } })),
    // The check is recorded in the health like any other call.
    onSettled: () => queryClient.invalidateQueries({ queryKey: [...KEY, "health"] }),
  });
}

export type FailureKind = Schemas["FailureKindDto"];

/** Which kinds of failure the platform records at all (courier bookings and webhooks are not, see the notes). */
export function useFailureKinds() {
  return useQuery({ queryKey: [...KEY, "kinds"], queryFn: async () => unwrap(await api.GET("/api/v1/system/jobs/kinds")) });
}
