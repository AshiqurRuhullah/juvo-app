"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";
import type { TicketFilters } from "./logic";
import { toTicketQuery } from "./logic";

/** Typed access to the support endpoints (module Y, docs/support-api.md). Every call goes through the BFF. */
export type TicketListItem = Schemas["TicketListItemDto"];
export type TicketDetail = Schemas["TicketDetailDto"];
export type TicketSummary = Schemas["TicketSummaryDto"];
export type TicketMessage = Schemas["TicketMessageDto"];
export type CannedReply = Schemas["CannedReplyDto"];
export type BulkResult = Schemas["BulkResultDto"];
export type MyTicket = Schemas["MyTicketDto"];
export type MyTicketListItem = Schemas["MyTicketListItemDto"];
export type TicketUpload = Schemas["TicketUploadDto"];
export type TicketStatus = Schemas["TicketStatus"];
export type TicketPriority = Schemas["TicketPriority"];

// The generated schema names the report's { key, count } rows "CountDto", which clashes with the stock-count DTO of the same name:
// the report rows are typed here from docs/support-api.md instead.
export type KeyCount = { key: string; count: number | string };
export type SupportReport = Omit<Schemas["SupportReportDto"], "openByStatus" | "openByPriority" | "byCategory"> & {
  openByStatus: KeyCount[];
  openByPriority: KeyCount[];
  byCategory: KeyCount[];
};

const KEY = ["support"] as const;

/* ---- Uploads ---------------------------------------------------------------------------------------------------------------- */

/** One file per call through the BFF (the typed client is JSON only). `scope` picks the staff or the customer upload endpoint. */
export async function uploadTicketFile(file: File, scope: "staff" | "customer"): Promise<TicketUpload> {
  const form = new FormData();
  form.append("file", file);
  const url = scope === "staff" ? "/api/bff/api/v1/support/tickets/uploads" : "/api/bff/api/v1/me/tickets/uploads";
  const response = await fetch(url, { method: "POST", body: form, credentials: "same-origin" });
  const text = await response.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;
  if (!response.ok) throw new ApiError(toProblem(parsed, response.status));
  return parsed as TicketUpload;
}

/* ---- Staff ------------------------------------------------------------------------------------------------------------------ */

export function useTickets(args: { filters: TicketFilters; page: number; pageSize: number; search?: string; sort?: string; me?: string | null }) {
  const query = toTicketQuery(args.filters, { page: args.page, pageSize: args.pageSize, search: args.search, sort: args.sort, me: args.me });
  return useQuery({
    queryKey: [...KEY, "list", query],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/support/tickets", { params: { query: query as never } })),
  });
}

export const useTicketSummary = () => useQuery({ queryKey: [...KEY, "summary"], queryFn: async () => unwrap(await api.GET("/api/v1/support/tickets/summary")) });

export function useTicket(id: string) {
  return useQuery({
    queryKey: [...KEY, "detail", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/support/tickets/{id}", { params: { path: { id } } })),
  });
}

function useAfterChange() {
  const queryClient = useQueryClient();
  return (id: string | null, detail?: TicketDetail) => {
    if (id && detail) queryClient.setQueryData([...KEY, "detail", id], detail);
    return Promise.all([queryClient.invalidateQueries({ queryKey: [...KEY, "list"] }), queryClient.invalidateQueries({ queryKey: [...KEY, "summary"] }), queryClient.invalidateQueries({ queryKey: [...KEY, "report"] })]);
  };
}

export type StaffCreate = {
  mobile: string;
  customerName: string;
  email: string;
  subject: string;
  message: string;
  channel: Schemas["TicketChannel"];
  priority: TicketPriority;
  category: string;
  orderNumber: string;
  assigneeId: string | null;
  attachmentPaths: string[];
};

export function useCreateTicket() {
  const after = useAfterChange();
  return useMutation({
    mutationFn: async (v: StaffCreate) =>
      unwrap(
        await api.POST("/api/v1/support/tickets", {
          body: {
            mobile: v.mobile.trim(),
            customerName: v.customerName.trim() || null,
            email: v.email.trim() || null,
            subject: v.subject.trim(),
            message: v.message.trim(),
            channel: v.channel,
            priority: v.priority,
            category: v.category || "other",
            orderNumber: v.orderNumber.trim() || null,
            assigneeId: v.assigneeId,
            attachmentPaths: v.attachmentPaths,
          },
        }),
      ),
    onSuccess: (detail) => after(detail.id, detail),
  });
}

export type StaffAction =
  | { action: "reply"; text: string; internal: boolean; setStatus?: TicketStatus | null; attachmentPaths: string[] }
  | { action: "assign"; assigneeId: string | null }
  | { action: "priority"; priority: TicketPriority }
  | { action: "status"; status: TicketStatus; reason?: string }
  | { action: "resolve"; reason?: string }
  | { action: "close"; reason?: string }
  | { action: "reopen"; reason?: string };

/** One change to a ticket. Every endpoint answers the updated detail, which replaces the cached one. */
export function useTicketAction(id: string) {
  const after = useAfterChange();
  return useMutation({
    mutationFn: async (v: StaffAction): Promise<TicketDetail> => {
      const p = { params: { path: { id } } };
      switch (v.action) {
        case "reply":
          return unwrap(await api.POST("/api/v1/support/tickets/{id}/reply", { ...p, body: { text: v.text.trim(), internal: v.internal, setStatus: v.setStatus ?? null, attachmentPaths: v.attachmentPaths } }));
        case "assign":
          return unwrap(await api.POST("/api/v1/support/tickets/{id}/assign", { ...p, body: { assigneeId: v.assigneeId } }));
        case "priority":
          return unwrap(await api.PUT("/api/v1/support/tickets/{id}/priority", { ...p, body: { priority: v.priority } }));
        case "status":
          return unwrap(await api.POST("/api/v1/support/tickets/{id}/status", { ...p, body: { status: v.status, reason: v.reason?.trim() || null } }));
        case "resolve":
          return unwrap(await api.POST("/api/v1/support/tickets/{id}/resolve", { ...p, body: { reason: v.reason?.trim() || null } }));
        case "close":
          return unwrap(await api.POST("/api/v1/support/tickets/{id}/close", { ...p, body: { reason: v.reason?.trim() || null } }));
        case "reopen":
          return unwrap(await api.POST("/api/v1/support/tickets/{id}/reopen", { ...p, body: { reason: v.reason?.trim() || null } }));
      }
    },
    onSuccess: (detail) => after(id, detail),
  });
}

export function useBulkTickets() {
  const after = useAfterChange();
  return useMutation({
    mutationFn: async (v: { action: "assign" | "close"; ids: string[]; assigneeId?: string | null; reason?: string }) =>
      unwrap(await api.POST("/api/v1/support/tickets/bulk", { body: { ids: v.ids, action: v.action, assigneeId: v.assigneeId ?? null, reason: v.reason?.trim() || null } })),
    onSuccess: () => after(null),
  });
}

/** Active staff for the assignee choices. Only asked for when a screen needs them and the person may view users (the API has no assignable-staff list). */
export function useStaffForAssign(enabled: boolean) {
  return useQuery({
    queryKey: ["ums", "users", "assignable"],
    enabled,
    staleTime: 5 * 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/ums/users", { params: { query: { Page: 1, PageSize: 100, isActive: true } } })),
  });
}

/* ---- Canned replies --------------------------------------------------------------------------------------------------------- */

export function useCannedReplies(params: { language?: string; includeInactive?: boolean; enabled?: boolean }) {
  return useQuery({
    queryKey: [...KEY, "canned", params.language ?? "", !!params.includeInactive],
    enabled: params.enabled ?? true,
    staleTime: 60_000,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/support/canned-replies", { params: { query: { ...(params.language ? { language: params.language } : {}), ...(params.includeInactive ? { includeInactive: true } : {}) } } })),
  });
}

export type CannedInput = { title: string; body: string; language: string; category: string; isActive: boolean };

export function useSaveCanned(id?: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (v: CannedInput) => {
      const body = { title: v.title.trim(), body: v.body.trim(), language: v.language, category: v.category.trim() || null, isActive: v.isActive };
      return id
        ? unwrap(await api.PUT("/api/v1/support/canned-replies/{id}", { params: { path: { id } }, body }))
        : unwrap(await api.POST("/api/v1/support/canned-replies", { body }));
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "canned"] }),
  });
}

export function useDeleteCanned() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (id: string) => {
      const result = await api.DELETE("/api/v1/support/canned-replies/{id}", { params: { path: { id } } });
      if (result.error !== undefined) throw new ApiError(toProblem(result.error, result.response.status));
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "canned"] }),
  });
}

/** Fills a saved reply's placeholders for one ticket on the server (the same rules as when sending). */
export function useRenderCanned() {
  return useMutation({
    mutationFn: async (v: { cannedReplyId: string; ticketId: string }) =>
      unwrap(await api.POST("/api/v1/support/canned-replies/render", { body: { cannedReplyId: v.cannedReplyId, body: null, ticketId: v.ticketId } })),
  });
}

/* ---- Report ----------------------------------------------------------------------------------------------------------------- */

export function useSupportReport(params: { from: string; to: string; branchId?: string; enabled?: boolean }) {
  return useQuery({
    queryKey: [...KEY, "report", params.from, params.to, params.branchId ?? ""],
    enabled: params.enabled ?? true,
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/support/report", { params: { query: { from: params.from, to: params.to, ...(params.branchId ? { branchId: params.branchId } : {}) } } })) as unknown as SupportReport,
  });
}

/* ---- Customer --------------------------------------------------------------------------------------------------------------- */

export function useMyTickets(params: { status?: TicketStatus; page: number; pageSize: number }) {
  return useQuery({
    queryKey: [...KEY, "mine", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/tickets", { params: { query: { Page: params.page, PageSize: params.pageSize, ...(params.status ? { status: params.status } : {}) } } })),
  });
}

export function useMyTicket(id: string) {
  return useQuery({
    queryKey: [...KEY, "mine-one", id],
    enabled: !!id,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/tickets/{id}", { params: { path: { id } } })),
  });
}

export function useCreateMyTicket() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (v: { subject: string; message: string; category: string; orderNumber: string; attachmentPaths: string[] }) =>
      unwrap(
        await api.POST("/api/v1/me/tickets", {
          body: { subject: v.subject.trim(), message: v.message.trim(), category: v.category || "other", orderNumber: v.orderNumber.trim() || null, attachmentPaths: v.attachmentPaths },
        }),
      ),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: [...KEY, "mine"] }),
  });
}

export type MyAction = { action: "reply"; text: string; attachmentPaths: string[] } | { action: "close" } | { action: "reopen" };

export function useMyTicketAction(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (v: MyAction): Promise<MyTicket> => {
      const p = { params: { path: { id } } };
      switch (v.action) {
        case "reply":
          return unwrap(await api.POST("/api/v1/me/tickets/{id}/reply", { ...p, body: { text: v.text.trim(), attachmentPaths: v.attachmentPaths } }));
        case "close":
          return unwrap(await api.POST("/api/v1/me/tickets/{id}/close", p));
        case "reopen":
          return unwrap(await api.POST("/api/v1/me/tickets/{id}/reopen", p));
      }
    },
    onSuccess: (detail) => {
      queryClient.setQueryData([...KEY, "mine-one", id], detail);
      return queryClient.invalidateQueries({ queryKey: [...KEY, "mine"] });
    },
  });
}

/* ---- Public contact form ---------------------------------------------------------------------------------------------------- */

export type ContactInput = { name: string; mobile: string; email: string; subject: string; message: string; category: string; orderNumber: string };

export function useSendContact() {
  return useMutation({
    mutationFn: async (v: ContactInput) =>
      unwrap(
        await api.POST("/api/v1/store/contact", {
          body: {
            name: v.name.trim(),
            mobile: v.mobile.trim(),
            email: v.email.trim() || null,
            subject: v.subject.trim(),
            message: v.message.trim(),
            category: v.category || "other",
            orderNumber: v.orderNumber.trim() || null,
          },
        }),
      ),
  });
}
