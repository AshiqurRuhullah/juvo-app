"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { toContactBody, toInboxQuery, type ContactFields, type InboxFilters, type MessageStatus } from "./logic";

/** Typed access to the contact endpoints (docs/content-api.md section 7). Every call goes through the BFF. */
export type MessageListItem = Schemas["ContactMessageListItemDto"];
export type MessageDetail = Schemas["ContactMessageDto"];

const KEY = ["contact-messages"] as const;

/**
 * The receipt. The generated client types this answer as the support ticket receipt (`number`) because both DTOs are named
 * `ContactReceiptDto` (contract gap, docs/contact-ui-notes.md); the real answer is `{ reference }`.
 */
export type ContactReceipt = { reference: string };

/** Visitor: send a message. `website` is the honeypot and is always empty for a real person. */
export function useSendContactMessage() {
  return useMutation({
    mutationFn: async (args: { form: ContactFields; honeypot: string }) =>
      (unwrap(await api.POST("/api/v1/store/content/contact", { body: toContactBody(args.form, "/contact", args.honeypot) })) as unknown) as ContactReceipt,
  });
}

export function useMessages(args: { filters: InboxFilters; page: number; pageSize: number; search?: string }) {
  const query = toInboxQuery(args.filters, args);
  return useQuery({
    queryKey: [...KEY, "list", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/content/messages", { params: { query: query as never } })),
  });
}

/** How many messages are New right now (one row requested, only the total is used). */
export function useNewCount() {
  return useQuery({
    queryKey: [...KEY, "new-count"],
    queryFn: async () => unwrap(await api.GET("/api/v1/content/messages", { params: { query: { Page: 1, PageSize: 1, status: "New" } } })),
    select: (data) => Number(data.total),
  });
}

export function useMessage(id: string) {
  return useQuery({
    queryKey: [...KEY, "detail", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/content/messages/{id}", { params: { path: { id } } })),
  });
}

function useAfterChange() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: KEY });
}

export function useSetStatus(id: string) {
  const after = useAfterChange();
  return useMutation({
    mutationFn: async (status: MessageStatus) => unwrap(await api.POST("/api/v1/content/messages/{id}/status", { params: { path: { id } }, body: { status } })),
    onSuccess: after,
  });
}

export function useAddNote(id: string) {
  const after = useAfterChange();
  return useMutation({
    mutationFn: async (note: string) => unwrap(await api.POST("/api/v1/content/messages/{id}/note", { params: { path: { id } }, body: { note: note.trim() } })),
    onSuccess: after,
  });
}

export function useArchive(id: string) {
  const after = useAfterChange();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/content/messages/{id}/archive", { params: { path: { id } } })),
    onSuccess: after,
  });
}
