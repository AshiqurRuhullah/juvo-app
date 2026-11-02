"use client";

import { useTranslations } from "next-intl";
import type { ApiProblem } from "./types";

/**
 * Friendly, translated text for an API problem. Looks up errors.<CODE with dots as underscores> (for example
 * errors.AUTH_INVALID_CREDENTIALS); falls back to a generic message by status. Never shows raw server text or stack traces.
 */
export function useProblemMessage() {
  const t = useTranslations("errors");
  return (problem: ApiProblem | undefined | null): string => {
    if (!problem) return t("GENERAL_UNEXPECTED");
    const key = problem.code?.replaceAll(".", "_");
    if (key && t.has(key)) return t(key);
    if (problem.status === 0 || problem.code === "NETWORK") return t("NETWORK");
    if (problem.status === 429) return t("GENERAL_TOO_MANY_REQUESTS");
    if (problem.status === 403) return t("AUTH_FORBIDDEN");
    if (problem.status === 404) return t("GENERAL_NOT_FOUND");
    if (problem.status && problem.status >= 500) return t("GENERAL_UNEXPECTED");
    return t("GENERAL_BAD_REQUEST");
  };
}
