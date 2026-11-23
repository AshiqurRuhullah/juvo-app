"use client";

import { useLocale, useTranslations } from "next-intl";
import type { ApiProblem } from "@/lib/api/types";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import type { Locale } from "./logic";

export function useUiLocale(): Locale {
  return useLocale() === "bn" ? "bn" : "en";
}

/** The heading of a group; a group the screen has no text for yet shows the API's own name. */
export function useGroupLabel() {
  const t = useTranslations("settings.groups");
  return (group: string): string => (t.has(group) ? t(group) : group);
}

/**
 * Staff wording for a refused request. A validation answer (SETTINGS.INVALID_VALUE) carries the API's own sentence for the field
 * (`errors.value`), which says exactly what is wrong, so it is shown first and plainly; the other codes use the screen's texts.
 */
export function useSettingsProblem() {
  const t = useTranslations("settings.errors");
  const fallback = useProblemMessage();
  return (problem: ApiProblem | null | undefined): string => {
    const fields = Object.values(problem?.errors ?? {}).flat();
    if (problem?.code === "SETTINGS.INVALID_VALUE" && fields.length) return fields.join(" ");
    const key = problem?.code?.replaceAll(".", "_");
    if (key && t.has(key)) return t(key);
    return fields.length ? fields.join(" ") : fallback(problem);
  };
}
