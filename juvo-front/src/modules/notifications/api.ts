"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";

/** Typed access to notification templates, delivery logs and channels (Step 9-a, module K). */
export type NotificationChannel = Schemas["NotificationChannel"];
export type NotificationStatus = Schemas["NotificationStatus"];
export type Template = Schemas["TemplateDto"];
export type TemplateVersion = Schemas["TemplateVersionDto"];
export type TemplatePreview = Schemas["PreviewDto"];
export type NotificationLog = Schemas["NotificationLogDto"];
export type ChannelSettings = Schemas["ChannelSettingsDto"];

export const TEMPLATE_CHANNELS: NotificationChannel[] = ["Sms", "Email", "WhatsApp"];
export const LOG_CHANNELS: NotificationChannel[] = ["Sms", "Email", "WhatsApp", "InApp"];
export const LOG_STATUSES: NotificationStatus[] = ["Pending", "Sent", "Failed", "DeadLetter", "Skipped"];
export const LANGUAGES = ["en", "bn"] as const;
export type Language = (typeof LANGUAGES)[number];

const clean = <T extends Record<string, unknown>>(value: T) => Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

export const useTemplates = () => useQuery({ queryKey: ["notifications", "templates"], queryFn: async () => unwrap(await api.GET("/api/v1/notifications/templates")) });

export const useTemplateHistory = (code: string, channel: NotificationChannel, language: string, enabled = true) =>
  useQuery({
    queryKey: ["notifications", "history", code, channel, language],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/notifications/templates/{code}/{channel}/{language}/history", { params: { path: { code, channel, language } } })),
  });

export function useSaveTemplate() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { code: string; channel: NotificationChannel; language: string; subject: string | null; body: string; isActive: boolean }) =>
      unwrap(await api.PUT("/api/v1/notifications/templates/{code}/{channel}/{language}", { params: { path: { code: vars.code, channel: vars.channel, language: vars.language } }, body: { subject: vars.subject, body: vars.body, isActive: vars.isActive } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["notifications"] }),
  });
}

export const usePreviewTemplate = () =>
  useMutation({
    mutationFn: async (vars: { code: string; channel: NotificationChannel; language: string; subject: string | null; body: string | null }) =>
      unwrap(await api.POST("/api/v1/notifications/templates/preview", { body: { code: vars.code, channel: vars.channel, language: vars.language, subject: vars.subject, body: vars.body, variables: null } })),
  });

export type LogFilters = { status?: string; channel?: string; template?: string; recipient?: string; from?: string; to?: string };

export function useNotificationLogs(params: { page: number; pageSize: number; sort?: string; search?: string } & LogFilters) {
  return useQuery({
    queryKey: ["notifications", "logs", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/notifications/logs", {
          params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, status: params.status as NotificationStatus | undefined, channel: params.channel as NotificationChannel | undefined, template: params.template, recipient: params.recipient, from: params.from, to: params.to }) },
        }),
      ),
  });
}

export function useResendLog() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/notifications/logs/{id}/resend", { params: { path: { id } } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["notifications", "logs"] }),
  });
}

export const useChannels = (enabled = true) => useQuery({ queryKey: ["notifications", "channels"], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/notifications/channels")) });

export function useSaveChannels() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (body: { smsEnabled: boolean | null; emailEnabled: boolean | null; whatsAppEnabled: boolean | null; defaultLanguage: string | null }) => unwrap(await api.PUT("/api/v1/notifications/channels", { body })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["notifications", "channels"] }),
  });
}
