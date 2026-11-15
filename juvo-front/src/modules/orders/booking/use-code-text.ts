"use client";

import { useTranslations } from "next-intl";

/** Friendly text for a stable API error code that arrived inside a result (not as a failed request), for example one order of a bulk booking. */
export function useCodeText() {
  const errors = useTranslations("errors");
  const cart = useTranslations("cart");
  return (code: string | null | undefined, fallback?: string | null): string => {
    const key = code?.replaceAll(".", "_");
    if (key && cart.has(`errors.${key}`)) return cart(`errors.${key}`);
    if (key && errors.has(key)) return errors(key);
    return fallback ?? errors("GENERAL_UNEXPECTED");
  };
}
