"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { api, ApiError, unwrap } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toast } from "@/components/ui/toaster";

/**
 * Typed access to the order endpoints (Step 8-a): the shop (quote, checkout, tracking, my orders), the staff order screens, the
 * social order desk and the fraud review. Every call goes through the BFF, which adds the token, the active branch and (for the
 * checkout) the guest cart token and the Idempotency-Key.
 */
export type OrderStatus = Schemas["OrderStatus"];
export type PaymentStatus = Schemas["PaymentStatus"];
export type DeliveryStatus = Schemas["DeliveryStatus"];
export type FraudState = Schemas["FraudState"];
export type OrderSource = Schemas["OrderSource"];
export type OrderTrack = Schemas["OrderTrack"];
export type NoteKind = Schemas["NoteKind"];
export type FraudAction = Schemas["FraudAction"];
export type RuleAction = Schemas["RuleAction"];
export type BlacklistKind = Schemas["BlacklistKind"];
export type CheckoutQuote = Schemas["CheckoutQuoteDto"];
export type CheckoutRequest = Schemas["CheckoutRequest"];
export type CheckoutResult = Schemas["CheckoutResultDto"];
export type CheckoutAddress = Schemas["CheckoutAddress"];
export type Tracking = Schemas["TrackingDto"];
export type MyOrderListItem = Schemas["MyOrderListItemDto"];
export type MyOrder = Schemas["MyOrderDto"];
export type OrderListItem = Schemas["OrderListItemDto"];
export type OrderDetail = Schemas["OrderDetailDto"];
export type OrderLine = Schemas["OrderLineDto"];
export type TimelineEntry = Schemas["TimelineDto"];
export type OrderNote = Schemas["NoteDto"];
export type RiskFlag = Schemas["RiskFlagDto"];
export type FraudLog = Schemas["FraudLogDto"];
export type CustomerStats = Schemas["CustomerOrderStatsDto"];
export type PackingSlip = Schemas["PackingSlipDto"];
export type BulkResult = Schemas["BulkOrderResult"];
export type ManualOrderRequest = Schemas["ManualOrderRequest"];
export type SourceCount = Schemas["OrderSourceCountDto"];
export type ReviewItem = Schemas["ReviewItemDto"];
export type Blacklist = Schemas["BlacklistDto"];
export type FraudRule = Schemas["FraudRuleDto"];

export const ORDER_STATUSES: OrderStatus[] = ["Placed", "Confirmed", "Packed", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered", "Cancelled", "DeliveryFailed", "ReturnInProgress", "Returned"];
export const PAYMENT_STATUSES: PaymentStatus[] = ["Pending", "Paid", "Failed", "Cancelled", "UnderReview", "Rejected", "PartiallyRefunded", "Refunded", "CollectedByCourier", "Settled"];
export const DELIVERY_STATUSES: DeliveryStatus[] = ["NotShipped", "ReadyToShip", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered", "Failed", "Returned", "Cancelled"];
export const FRAUD_STATES: FraudState[] = ["None", "Flagged", "InReview", "AdvanceRequested", "Approved", "Rejected"];
export const HELD_STATES: FraudState[] = ["Flagged", "InReview", "AdvanceRequested"];
export const ORDER_SOURCES: OrderSource[] = ["Web", "Facebook", "WhatsApp", "Phone", "WalkIn", "Other"];
export const DESK_SOURCES: OrderSource[] = ["Facebook", "WhatsApp", "Phone", "WalkIn", "Other"];
export const BLACKLIST_KINDS: BlacklistKind[] = ["Mobile", "Email", "Address", "Device"];
export const RULE_ACTIONS: RuleAction[] = ["Flag", "Hold", "RequireAdvance", "Reject"];
export const CUSTOMER_FILTERS = ["active", "delivered", "cancelled", "returned"] as const;
export type CustomerFilter = (typeof CUSTOMER_FILTERS)[number];

const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

/* ---- Shop: quote, checkout, tracking ---------------------------------------------------------------------------------------- */

export type QuoteInput = { addressId?: string | null; address?: Partial<CheckoutAddress> | null; paymentMethod?: string | null; mobile?: string | null };

/** The server's own working-out of the basket (goods, delivery, advance, payment methods). Asked again on every change. */
export function useCheckoutQuote(input: QuoteInput, enabled: boolean, cartKey: string) {
  return useQuery({
    queryKey: ["orders", "quote", input, cartKey],
    enabled,
    retry: false,
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.POST("/api/v1/store/checkout/quote", {
          body: { addressId: input.addressId ?? null, address: (input.address as CheckoutAddress | null | undefined) ?? null, paymentMethod: input.paymentMethod ?? null, mobile: input.mobile ?? null },
        }),
      ),
  });
}

export function useRequestOrderOtp() {
  return useMutation({
    mutationFn: async (mobile: string) => unwrap(await api.POST("/api/v1/auth/otp/request", { body: { mobile, purpose: "ConfirmOrder", channel: "sms", email: null } })),
  });
}

/** Places the order. The Idempotency-Key is chosen by the caller and kept the same on every retry of one attempt. */
export function usePlaceOrder() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (vars: { body: CheckoutRequest; key: string }) =>
      unwrap(await api.POST("/api/v1/store/checkout", { body: { ...vars.body, idempotencyKey: vars.key }, headers: { "Idempotency-Key": vars.key } })),
    onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ["store", "cart"] }), queryClient.invalidateQueries({ queryKey: ["store", "me"] })]),
  });
}

export type TrackQuery = { number: string; mobile: string } | { token: string };

export function useTracking(query: TrackQuery | null) {
  return useQuery({
    queryKey: ["orders", "track", query],
    enabled: !!query,
    retry: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/store/orders/track", { params: { query: query! } })),
  });
}

/* ---- Customer panel --------------------------------------------------------------------------------------------------------- */

export function useMyOrders(params: { filter?: CustomerFilter; page: number; pageSize: number }) {
  return useQuery({
    queryKey: ["orders", "mine", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/me/orders", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, filter: params.filter }) } })),
  });
}

export const useMyOrder = (id: string) => useQuery({ queryKey: ["orders", "mine", id], queryFn: async () => unwrap(await api.GET("/api/v1/me/orders/{id}", { params: { path: { id } } })) });

export function useCancelMyOrder(id: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (reason: string | null) => unwrap(await api.POST("/api/v1/me/orders/{id}/cancel", { params: { path: { id } }, body: { reason } })),
    onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ["orders", "mine"] }), queryClient.invalidateQueries({ queryKey: ["store", "me", "summary"] })]),
  });
}

/* ---- Staff: list and detail ------------------------------------------------------------------------------------------------- */

export type OrderFilters = {
  status?: OrderStatus;
  paymentStatus?: PaymentStatus;
  deliveryStatus?: DeliveryStatus;
  branchId?: string;
  source?: OrderSource;
  fraudState?: FraudState;
  from?: string;
  to?: string;
  held?: boolean;
};
export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

export function useOrders(params: ListParams & OrderFilters) {
  return useQuery({
    queryKey: ["orders", "list", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/orders", {
          params: {
            query: clean({
              Page: params.page,
              PageSize: params.pageSize,
              Sort: params.sort,
              Search: params.search,
              status: params.status,
              paymentStatus: params.paymentStatus,
              deliveryStatus: params.deliveryStatus,
              branchId: params.branchId,
              source: params.source,
              fraudState: params.fraudState,
              from: params.from,
              to: params.to,
              held: params.held,
            }),
          },
        }),
      ),
  });
}

export const useOrder = (id: string) => useQuery({ queryKey: ["orders", "detail", id], queryFn: async () => unwrap(await api.GET("/api/v1/orders/{id}", { params: { path: { id } } })) });

export function useSourceSummary(range: { from?: string; to?: string }, enabled: boolean) {
  return useQuery({
    queryKey: ["orders", "sources", range],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/orders/sources/summary", { params: { query: clean(range) } })),
  });
}

export const usePackingSlip = (id: string) => useQuery({ queryKey: ["orders", "slip", id], queryFn: async () => unwrap(await api.GET("/api/v1/orders/{id}/packing-slip", { params: { path: { id } } })) });

/* ---- Staff: actions --------------------------------------------------------------------------------------------------------- */

function useOrdersMutation<TVars, TData>(run: (vars: TVars) => Promise<TData>) {
  const queryClient = useQueryClient();
  return useMutation({ mutationFn: run, onSuccess: () => Promise.all([queryClient.invalidateQueries({ queryKey: ["orders"] }), queryClient.invalidateQueries({ queryKey: ["fraud"] })]) });
}

export type OrderAction =
  | { type: "confirm"; advanceReceived?: boolean }
  | { type: "pack" }
  | { type: "cancel"; reason: string }
  | { type: "transition"; to: OrderStatus; reason?: string | null }
  | { type: "note"; kind: NoteKind; text: string }
  | { type: "reassign"; branchId: string; reason?: string | null };

export function useOrderAction(id: string) {
  const path = { params: { path: { id } } };
  return useOrdersMutation(async (action: OrderAction) => {
    switch (action.type) {
      case "confirm":
        return unwrap(await api.POST("/api/v1/orders/{id}/confirm", { ...path, body: { advanceReceived: !!action.advanceReceived } }));
      case "pack":
        return unwrap(await api.POST("/api/v1/orders/{id}/pack", path));
      case "cancel":
        return unwrap(await api.POST("/api/v1/orders/{id}/cancel", { ...path, body: { reason: action.reason } }));
      case "transition":
        return unwrap(await api.POST("/api/v1/orders/{id}/transition", { ...path, body: { to: action.to, reason: action.reason ?? null } }));
      case "note":
        return unwrap(await api.POST("/api/v1/orders/{id}/notes", { ...path, body: { kind: action.kind, text: action.text } }));
      case "reassign":
        return unwrap(await api.POST("/api/v1/orders/{id}/reassign", { ...path, body: { branchId: action.branchId, reason: action.reason ?? null } }));
    }
  });
}

export const useBulkAction = () =>
  useOrdersMutation(async (body: { action: "confirm" | "cancel" | "print"; ids: string[]; reason?: string | null; advanceReceived?: boolean }) =>
    unwrap(await api.POST("/api/v1/orders/bulk", { body: { action: body.action, ids: body.ids, reason: body.reason ?? null, advanceReceived: !!body.advanceReceived } })),
  );

export const useCreateManualOrder = () => useOrdersMutation(async (body: ManualOrderRequest) => unwrap(await api.POST("/api/v1/orders/manual", { body })));

/* ---- Fraud ------------------------------------------------------------------------------------------------------------------ */

export function useFraudReviews(params: { page: number; pageSize: number; state?: FraudState }) {
  return useQuery({
    queryKey: ["fraud", "reviews", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/fraud/reviews", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, state: params.state }) } })),
  });
}

export const useFraudDecision = () =>
  useOrdersMutation(async (vars: { orderId: string; action: FraudAction; note?: string | null }) =>
    unwrap(await api.POST("/api/v1/fraud/reviews/{orderId}/decide", { params: { path: { orderId: vars.orderId } }, body: { action: vars.action, note: vars.note ?? null } })),
  );

export const useBlacklist = (params: { page: number; pageSize: number; kind?: BlacklistKind }, enabled = true) =>
  useQuery({
    queryKey: ["fraud", "blacklist", params],
    enabled,
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/fraud/blacklist", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, kind: params.kind }) } })),
  });

export const useAddBlacklist = () => useOrdersMutation(async (body: { kind: BlacklistKind; value: string; note: string | null }) => unwrap(await api.POST("/api/v1/fraud/blacklist", { body })));
export const useRemoveBlacklist = () => useOrdersMutation(async (id: string) => unwrap(await api.DELETE("/api/v1/fraud/blacklist/{id}", { params: { path: { id } } })));

export const useFraudRules = (enabled = true) => useQuery({ queryKey: ["fraud", "rules"], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/fraud/rules")) });
export const useUpdateFraudRule = () =>
  useOrdersMutation(async (vars: { code: string; enabled: boolean; threshold: number; action: RuleAction }) =>
    unwrap(await api.PUT("/api/v1/fraud/rules/{code}", { params: { path: { code: vars.code } }, body: { enabled: vars.enabled, threshold: vars.threshold, action: vars.action } })),
  );

export const useCustomerStats = (customerId: string | null) =>
  useQuery({ queryKey: ["fraud", "stats", customerId], enabled: !!customerId, queryFn: async () => unwrap(await api.GET("/api/v1/fraud/customers/{customerId}/stats", { params: { path: { customerId: customerId! } } })) });

/* ---- Errors ----------------------------------------------------------------------------------------------------------------- */

/** Friendly text for an API problem (errors.<CODE>), as a string, without showing it. */
export function useOrderErrorText() {
  const message = useProblemMessage();
  const cart = useTranslations("cart");
  return (error: unknown): string => {
    const problem = error instanceof ApiError ? error.problem : null;
    // Cart, coupon and delivery codes (CART.*, COUPON.*, DELIVERY.*) have their own wording in the cart texts.
    const key = problem?.code?.replaceAll(".", "_");
    if (key && cart.has(`errors.${key}`)) return cart(`errors.${key}`);
    return message(problem);
  };
}

/** Shows the friendly text as a toast and returns the field errors, if any, for the form. */
export function useReportOrderError() {
  const text = useOrderErrorText();
  return (error: unknown): Record<string, string> => {
    toast.error(text(error));
    return error instanceof ApiError ? fieldErrors(error.problem) : {};
  };
}

export const problemOf = (error: unknown) => (error instanceof ApiError ? error.problem : null);

/** The signed-in customer's saved addresses, fetched only when someone is signed in (a guest would get a 401 and be sent to sign in). */
export const useSavedAddresses = (enabled: boolean) =>
  useQuery({ queryKey: ["store", "me", "addresses"], enabled, queryFn: async () => unwrap(await api.GET("/api/v1/me/addresses")) });

/** Staff look a customer up by mobile on the order desk (needs customers.customer.view; without it the desk simply finds or creates by mobile). */
export function useCustomerLookup(mobile: string, enabled: boolean) {
  return useQuery({
    queryKey: ["orders", "customer-lookup", mobile],
    enabled: enabled && mobile.length >= 6,
    retry: false,
    queryFn: async () => {
      const page = unwrap(await api.GET("/api/v1/customers", { params: { query: { Search: mobile, PageSize: 5 } } }));
      const tail = mobile.replace(/\D/g, "").slice(-10);
      return page.items.find((c) => c.mobile.replace(/\D/g, "").endsWith(tail)) ?? null;
    },
  });
}

export type DeskPlace = { countryCode: string | null; districtId: string | null; upazilaId: string | null };

/** The shop's delivery charge for the chosen items and place; the desk shows it and may override it when allowed. */
export function useDeskDeliveryQuote(place: DeskPlace | null, items: { variantId: string; quantity: number }[]) {
  return useQuery({
    queryKey: ["orders", "desk-delivery", place, items],
    enabled: !!place && items.length > 0,
    retry: false,
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.POST("/api/v1/store/delivery/quote", { body: { countryCode: place!.countryCode, districtId: place!.districtId, upazilaId: place!.upazilaId, items, subtotal: null, weightGrams: null } })),
  });
}
