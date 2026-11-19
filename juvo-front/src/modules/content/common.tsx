"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleCheck, FilePen, Lock } from "lucide-react";
import { Alert, Badge } from "@/components/ui/surfaces";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import type { ApiProblem } from "@/lib/api/types";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import type { ContentRights } from "./logic";

/** What this staff member may do with site content (all global permissions). Display only: the API still refuses. */
export function useContentRights(): ContentRights {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      pageView: canAnywhere(me, "content.page.view"),
      pageEdit: canAnywhere(me, "content.page.edit"),
      pagePublish: canAnywhere(me, "content.page.publish"),
      brandView: canAnywhere(me, "content.brand.view"),
      brandEdit: canAnywhere(me, "content.brand.edit"),
      socialEdit: canAnywhere(me, "content.social.edit"),
      badgeEdit: canAnywhere(me, "content.badge.edit"),
    }),
    [me],
  );
}

/** Staff wording for a refused request: this module's codes first, then the shared texts. The API's field lines follow, unchanged. */
export function useContentProblem() {
  const t = useTranslations("content.errors");
  const fallback = useProblemMessage();
  return (problem: ApiProblem | null | undefined): { text: string; lines: string[] } => {
    const key = problem?.code?.replaceAll(".", "_");
    const lines = Object.values(problem?.errors ?? {}).flat();
    if (key && t.has(key)) return { text: t(key), lines };
    return { text: fallback(problem), lines };
  };
}

export function ReadOnlyNote({ testId, children }: { testId: string; children: React.ReactNode }) {
  return (
    <Alert variant="info" data-testid={testId}>
      <span className="flex items-start gap-2">
        <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
        <span>{children}</span>
      </span>
    </Alert>
  );
}

/** Published or draft, always an icon and a word. */
export function PublishedBadge({ published }: { published: boolean }) {
  const t = useTranslations("content.status");
  const Icon = published ? CircleCheck : FilePen;
  return (
    <Badge variant={published ? "success" : "outline"} data-testid="page-status" data-status={published ? "published" : "draft"}>
      <Icon className="size-3.5" aria-hidden />
      {published ? t("published") : t("draft")}
    </Badge>
  );
}

/** A failed save: the reason, then the API's own lines (for example which field held unsafe markup). */
export function FailureAlert({ failure, testId }: { failure: { text: string; lines: string[] } | null; testId: string }) {
  if (!failure) return null;
  return (
    <Alert variant="danger" data-testid={testId}>
      {failure.text}
      {failure.lines.map((line) => (
        <span key={line} className="block">
          {line}
        </span>
      ))}
    </Alert>
  );
}
