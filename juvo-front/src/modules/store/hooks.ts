"use client";

import { useLocale, useTranslations } from "next-intl";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";

/** Picks the Bengali name when the page is in Bengali and the product or category has one; otherwise the English name. */
export function usePickName() {
  const locale = useLocale();
  return (en: string, bn?: string | null) => (locale === "bn" && bn ? bn : en);
}

/**
 * Friendly text for an API problem in the shop. The cart and coupon codes (CART.*, COUPON.*) have their own wording in
 * `store.errors.<CODE with dots as underscores>`; everything else uses the shared error texts.
 */
export function useStoreError() {
  const t = useTranslations("store.errors");
  const shared = useProblemMessage();
  return (error: unknown): string => {
    const problem = error instanceof ApiError ? error.problem : null;
    const key = problem?.code?.replaceAll(".", "_");
    if (key && t.has(key)) return t(key);
    return shared(problem);
  };
}
