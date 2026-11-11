"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";

/**
 * Typed access to the payment endpoints (Step 9-a): the shop side (methods, initiate, sandbox pay page, confirm, status, manual
 * transfer, proof) and the staff side (payments, refunds, cash on delivery, advance, ledger, settings). Every call goes through
 * the BFF. The tracking token of a guest order is passed as the `token` query value; a signed-in customer needs none.
 */
export type PaymentStatus = Schemas["PaymentStatus"];
export type PaymentKind = Schemas["PaymentKind"];
export type PaymentSession = Schemas["PaymentSessionDto"];
export type PaymentMethodView = Schemas["PaymentMethodView"];
export type SandboxSession = Schemas["SandboxSessionDto"];
export type PaymentListItem = Schemas["PaymentListItemDto"];
export type PaymentDetail = Schemas["PaymentDetailDto"];
export type PaymentEvent = Schemas["PaymentEventDto"];
export type Refund = Schemas["RefundDto"];
export type RefundMode = Schemas["RefundMode"];
export type RefundState = Schemas["RefundState"];
export type Ledger = Schemas["LedgerDto"];
export type PaymentSettings = Schemas["PaymentSettingsDto"];

export const PAYMENT_STATUSES: PaymentStatus[] = ["Pending", "Paid", "Failed", "Cancelled", "UnderReview", "Rejected", "PartiallyRefunded", "Refunded", "CollectedByCourier", "Settled"];
export const PAYMENT_KINDS: PaymentKind[] = ["Online", "Manual", "Cod", "Advance"];
export const REFUND_STATES: RefundState[] = ["Pending", "Completed", "Failed"];

const clean = <T extends Record<string, unknown>>(value: T) => Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

/* ---- Shop ------------------------------------------------------------------------------------------------------------------- */

/** The methods the shop offers for a country and an amount (with the account details of a manual transfer). */
export function usePaymentMethods(amount: number, enabled = true) {
  return useQuery({
    queryKey: ["pay", "methods", amount],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/store/payment-methods", { params: { query: { country: "BD", amount } } })),
  });
}

export type Proof = { orderNumber: string; token: string | null };

/** Starts (or resumes) an online payment. `method` switches the order to another online method (a retry). */
export function useInitiatePayment() {
  return useMutation({
    mutationFn: async (vars: Proof & { method?: string | null; returnUrl: string }) =>
      unwrap(await api.POST("/api/v1/store/payments/initiate", { body: { orderId: null, orderNumber: vars.orderNumber, trackingToken: vars.token, method: vars.method ?? null, returnUrl: vars.returnUrl } })),
  });
}

/** Where a payment stands (cheap: reads the shop's own records). Polled while it is Pending. */
export function usePaymentStatus(paymentId: string | null, token: string | null, poll: boolean) {
  return useQuery({
    queryKey: ["pay", "status", paymentId],
    enabled: !!paymentId,
    retry: false,
    refetchInterval: (query) => (poll && (query.state.data?.status ?? "Pending") === "Pending" ? 2000 : false),
    queryFn: async () => unwrap(await api.GET("/api/v1/store/payments/{id}/status", { params: { path: { id: paymentId! }, query: token ? { token } : {} } })),
  });
}

/** The browser came back: the shop asks the gateway server to server. Nothing in the return address is used. */
export function useConfirmPayment() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { paymentId: string; token: string | null }) =>
      unwrap(await api.POST("/api/v1/store/payments/{id}/confirm", { params: { path: { id: vars.paymentId }, query: vars.token ? { token: vars.token } : {} } })),
    onSuccess: (session, vars) => {
      queryClient.setQueryData(["pay", "status", vars.paymentId], session);
      return queryClient.invalidateQueries({ queryKey: ["orders"] });
    },
  });
}

export function useManualPayment() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: Proof & { reference: string; payerName: string }) =>
      unwrap(await api.POST("/api/v1/store/payments/manual", { body: { orderId: null, orderNumber: vars.orderNumber, trackingToken: vars.token, reference: vars.reference, payerName: vars.payerName } })),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["orders"] }),
  });
}

/** Uploads the proof file of a manual transfer (multipart, up to 5 MB; JPEG, PNG, WebP or PDF). */
export function useUploadProof() {
  return useMutation({
    mutationFn: async (vars: { paymentId: string; token: string | null; file: File }): Promise<PaymentSession> => {
      const form = new FormData();
      form.append("file", vars.file);
      const query = vars.token ? `?token=${encodeURIComponent(vars.token)}` : "";
      const response = await fetch(`/api/bff/api/v1/store/payments/${encodeURIComponent(vars.paymentId)}/proof${query}`, { method: "POST", body: form });
      const data: unknown = await response.json().catch(() => null);
      if (!response.ok) throw new ApiError(toProblem(data, response.status));
      return data as PaymentSession;
    },
  });
}

/** The sandbox pay page's own session (what is being paid, whether it can still be used). */
export const useSandboxSession = (sessionId: string) =>
  useQuery({ queryKey: ["pay", "sandbox", sessionId], retry: false, queryFn: async () => unwrap(await api.GET("/api/v1/store/payments/sandbox/sessions/{sessionId}", { params: { path: { sessionId } } })) });

export type SandboxOutcome = "success" | "fail" | "cancel";

export function useSandboxDecision(sessionId: string) {
  return useMutation({
    mutationFn: async (outcome: SandboxOutcome) =>
      unwrap(await api.POST("/api/v1/store/payments/sandbox/sessions/{sessionId}/complete", { params: { path: { sessionId } }, body: { outcome, sendWebhook: true } })),
  });
}

/* ---- Staff: payments -------------------------------------------------------------------------------------------------------- */

export type PaymentFilters = { status?: string; method?: string; kind?: string; branchId?: string; from?: string; to?: string };
export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

export function usePayments(params: ListParams & PaymentFilters) {
  return useQuery({
    queryKey: ["payments", "list", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/payments", {
          params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, status: params.status as PaymentStatus | undefined, method: params.method, kind: params.kind as PaymentKind | undefined, branchId: params.branchId, from: params.from, to: params.to }) },
        }),
      ),
  });
}

export const usePayment = (id: string) => useQuery({ queryKey: ["payments", "detail", id], queryFn: async () => unwrap(await api.GET("/api/v1/payments/{id}", { params: { path: { id } } })) });

/** The payments of one order (shown on the staff order page). */
export const useOrderPayments = (orderId: string, enabled = true) =>
  useQuery({ queryKey: ["payments", "order", orderId], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/payments/orders/{orderId}", { params: { path: { orderId } } })) });

function usePaymentsMutation<TVars, TData>(run: (vars: TVars) => Promise<TData>) {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: run, onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ["payments"] }), queryClient.invalidateQueries({ queryKey: ["orders"] }), queryClient.invalidateQueries({ queryKey: ["billing"] })]) });
}

export const useApprovePayment = () => usePaymentsMutation(async (vars: { id: string; note: string | null }) => unwrap(await api.POST("/api/v1/payments/{id}/approve", { params: { path: { id: vars.id } }, body: { note: vars.note } })));
export const useRejectPayment = () => usePaymentsMutation(async (vars: { id: string; reason: string }) => unwrap(await api.POST("/api/v1/payments/{id}/reject", { params: { path: { id: vars.id } }, body: { reason: vars.reason } })));
export const useRecordAdvance = () =>
  usePaymentsMutation(async (vars: { orderId: string; amount: number; method: string; reference: string | null }) =>
    unwrap(await api.POST("/api/v1/payments/orders/{orderId}/advance", { params: { path: { orderId: vars.orderId } }, body: { amount: vars.amount, method: vars.method, reference: vars.reference } })),
  );
export const useCodCollect = () =>
  usePaymentsMutation(async (vars: { orderId: string; amount: number | null; reference: string | null }) =>
    unwrap(await api.POST("/api/v1/payments/orders/{orderId}/cod-collect", { params: { path: { orderId: vars.orderId } }, body: { amount: vars.amount, reference: vars.reference } })),
  );
export const useCodSettle = () =>
  usePaymentsMutation(async (vars: { orderId: string; reference: string | null }) => unwrap(await api.POST("/api/v1/payments/orders/{orderId}/cod-settle", { params: { path: { orderId: vars.orderId } }, body: { reference: vars.reference } })));

/* ---- Staff: refunds, ledger, settings --------------------------------------------------------------------------------------- */

export function useRefunds(params: { page: number; pageSize: number; status?: string; from?: string; to?: string; branchId?: string }) {
  return useQuery({
    queryKey: ["payments", "refunds", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/payments/refunds", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, status: params.status as RefundState | undefined, from: params.from, to: params.to, branchId: params.branchId }) } })),
  });
}

export const useCreateRefund = () =>
  usePaymentsMutation(async (vars: { paymentId: string; amount: number; mode: RefundMode; reason: string }) =>
    unwrap(await api.POST("/api/v1/payments/{id}/refunds", { params: { path: { id: vars.paymentId } }, body: { amount: vars.amount, mode: vars.mode, reason: vars.reason } })),
  );
export const useCompleteRefund = () => usePaymentsMutation(async (vars: { refundId: string; reference: string | null }) => unwrap(await api.POST("/api/v1/payments/refunds/{refundId}/complete", { params: { path: { refundId: vars.refundId } }, body: { reference: vars.reference } })));
export const useFailRefund = () => usePaymentsMutation(async (vars: { refundId: string; reason: string }) => unwrap(await api.POST("/api/v1/payments/refunds/{refundId}/fail", { params: { path: { refundId: vars.refundId } }, body: { reason: vars.reason } })));

export function useLedger(range: { from?: string; to?: string; branchId?: string }, enabled = true) {
  return useQuery({ queryKey: ["payments", "ledger", range], enabled, placeholderData: keepPreviousData, queryFn: async () => unwrap(await api.GET("/api/v1/payments/ledger", { params: { query: clean(range) } })) });
}

export const usePaymentSettings = (enabled = true) => useQuery({ queryKey: ["payments", "settings"], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/payments/settings")) });
export const useSavePaymentSettings = () =>
  usePaymentsMutation(async (body: { methods: { code: string; enabled: boolean }[]; codMaxAmount: number; manualInstructions: string | null; autoConfirmPaid: boolean }) => unwrap(await api.PUT("/api/v1/payments/settings", { body })));
