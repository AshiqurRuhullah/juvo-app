import type { PaymentSession, PaymentStatus } from "./api";

/**
 * Pure helpers for the payment screens. They hold no state and call nothing, so they are unit tested (tests/payments.test.ts).
 */

/** Cash on delivery and manual transfer are not paid through a gateway page. Everything else is an online method. */
export const isOnlineMethod = (code: string) => code !== "COD" && code !== "MANUAL";

/** An order still needs the customer to pay: an online or manual method whose payment is waiting or went wrong. */
export function needsPayment(method: string, paymentStatus: PaymentStatus, orderStatus: string): boolean {
  if (method === "COD") return false;
  if (["Cancelled", "Returned", "DeliveryFailed"].includes(orderStatus)) return false;
  return paymentStatus === "Pending" || (isOnlineMethod(method) && (paymentStatus === "Failed" || paymentStatus === "Cancelled"));
}

/** What the customer sees on the return page. */
export type Outcome = "paid" | "failed" | "cancelled" | "review" | "pending";

export function outcomeOf(session: Pick<PaymentSession, "status" | "orderPaymentStatus"> | undefined): Outcome {
  if (!session) return "pending";
  switch (session.status) {
    case "Paid":
    case "PartiallyRefunded":
    case "Refunded":
    case "Settled":
    case "CollectedByCourier":
      return "paid";
    case "Failed":
    case "Rejected":
      return "failed";
    case "Cancelled":
      return "cancelled";
    case "UnderReview":
      return "review";
    case "Pending":
      return "pending";
  }
}

/** Seconds left until `deadline` (never negative); null when there is no deadline. */
export function secondsLeft(deadline: string | null | undefined, now: number): number | null {
  if (!deadline) return null;
  const end = new Date(deadline).getTime();
  if (Number.isNaN(end)) return null;
  return Math.max(0, Math.floor((end - now) / 1000));
}

/** 29:05 for 1745 seconds; 1:02:03 beyond an hour. */
export function countdown(seconds: number): string {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = seconds % 60;
  const two = (n: number) => String(n).padStart(2, "0");
  return h > 0 ? `${h}:${two(m)}:${two(s)}` : `${m}:${two(s)}`;
}

/* ---- Where the customer is sent ---------------------------------------------------------------------------------------------- */

export const returnPath = (number: string) => `/pay/return?order=${encodeURIComponent(number)}`;
export const payPath = (number: string, token?: string | null, retry = false) => {
  const query = [token ? `token=${encodeURIComponent(token)}` : "", retry ? "retry=1" : ""].filter(Boolean).join("&");
  return `/pay/${encodeURIComponent(number)}${query ? `?${query}` : ""}`;
};
export const sandboxPath = (sessionId: string, paymentId: string) => `/pay/sandbox/${encodeURIComponent(sessionId)}?payment=${encodeURIComponent(paymentId)}`;

/** Adds one query value to a path or same-site address, keeping what is already there. Anything off-site is replaced by `fallback`. */
export function withParam(target: string | null | undefined, name: string, value: string, fallback: string): string {
  if (!target || !target.startsWith("/") || target.startsWith("//")) return fallback;
  const [path, query = ""] = target.split("?");
  const params = new URLSearchParams(query);
  params.set(name, value);
  return `${path}?${params.toString()}`;
}

/* ---- What the browser remembers between the pay pages (session storage, this tab only) --------------------------------------- */

export type PayState = { paymentId: string; token: string | null; deadline: string | null };
const key = (number: string) => `juvo_pay_${number}`;

export function loadPayState(storage: Pick<Storage, "getItem"> | null, number: string): PayState | null {
  try {
    const raw = storage?.getItem(key(number));
    if (!raw) return null;
    const value = JSON.parse(raw) as Partial<PayState>;
    return typeof value.paymentId === "string" ? { paymentId: value.paymentId, token: value.token ?? null, deadline: value.deadline ?? null } : null;
  } catch {
    return null;
  }
}

export function savePayState(storage: Pick<Storage, "setItem"> | null, number: string, state: PayState) {
  try {
    storage?.setItem(key(number), JSON.stringify(state));
  } catch {
    // Without storage the return page asks the server again (a signed-in customer) or shows the tracking link.
  }
}

/** An order has an invoice once it was confirmed: not while it is only placed, and never when it was cancelled before that. Asking earlier would only get a refusal. */
export const invoiceable = (orderStatus: string) => orderStatus !== "Placed" && orderStatus !== "Cancelled";

/** The proof file of a manual transfer: JPEG, PNG, WebP or PDF up to 5 MB (the API checks again). */
export const PROOF_TYPES = ["image/jpeg", "image/png", "image/webp", "application/pdf"];
export const PROOF_MAX_BYTES = 5 * 1024 * 1024;
export function proofProblem(file: { type: string; size: number }): "type" | "size" | null {
  if (!PROOF_TYPES.includes(file.type)) return "type";
  if (file.size > PROOF_MAX_BYTES) return "size";
  return null;
}

/** Which staff actions the server offers on a payment (`nextActions`), as a set of lower-case words. */
export function actionSet(nextActions: readonly string[]): ReadonlySet<string> {
  return new Set(nextActions.map((a) => a.toLowerCase()));
}
