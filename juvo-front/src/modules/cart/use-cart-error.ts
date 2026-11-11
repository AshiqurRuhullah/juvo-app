"use client";

import { useTranslations } from "next-intl";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toast } from "@/components/ui/toaster";

/**
 * Friendly text for an API error of the cart, coupon, delivery and customer-account endpoints. Looks up `cart.errors.<CODE>` first
 * (CART_STOCK_INSUFFICIENT, COUPON_EXPIRED ...) and falls back to the generic message for the problem.
 */
export function useCartErrorText() {
  const t = useTranslations("cart");
  const generic = useProblemMessage();
  return (error: unknown): string => {
    const problem = error instanceof ApiError ? error.problem : null;
    const key = problem?.code?.replaceAll(".", "_");
    if (key && t.has(`errors.${key}`)) return t(`errors.${key}`);
    return generic(problem);
  };
}

/** Shows the friendly text as a toast and returns the field errors (if any) so a form can mark the fields. */
export function useReportCartError() {
  const text = useCartErrorText();
  return (error: unknown): Record<string, string> => {
    toast.error(text(error));
    return error instanceof ApiError ? fieldErrors(error.problem) : {};
  };
}
