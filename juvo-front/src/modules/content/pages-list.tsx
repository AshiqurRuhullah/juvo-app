"use client";

import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { ExternalLink, Pencil } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { usePages } from "./api";
import { PublishedBadge, ReadOnlyNote, useContentProblem, useContentRights } from "./common";
import { isPageKey, publicPath } from "./logic";

/** The six site pages with their state. Each opens its own editor. */
export function PagesList() {
  const t = useTranslations("content.pages");
  const tn = useTranslations("content.pageNames");
  const format = useFormatter();
  const rights = useContentRights();
  const problem = useContentProblem();
  const pages = usePages();

  if (pages.isPending) {
    return (
      <div className="space-y-3" aria-busy="true">
        {[0, 1, 2].map((i) => (
          <Skeleton key={i} className="h-24 w-full" />
        ))}
      </div>
    );
  }
  if (pages.isError) {
    return <Alert variant="danger">{problem(pages.error instanceof ApiError ? pages.error.problem : null).text}</Alert>;
  }

  return (
    <div className="space-y-4">
      <p className="max-w-3xl text-muted">{t("subtitle")}</p>
      {!rights.pageEdit ? <ReadOnlyNote testId="pages-read-only">{t("readOnly")}</ReadOnlyNote> : null}
      <ul className="grid gap-3" data-testid="pages-list">
        {pages.data.map((page) => {
          const known = isPageKey(page.key);
          const name = known ? tn(page.key) : page.key;
          return (
            <li key={page.key}>
              <Card className="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between" data-testid={`page-row-${page.key}`}>
                <div className="min-w-0 space-y-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <h2 className="font-sans text-base font-semibold text-strong">{name}</h2>
                    <PublishedBadge published={page.isPublished} />
                  </div>
                  <p className="text-sm text-muted [overflow-wrap:anywhere]">{page.titleEn?.trim() ? page.titleEn : t("noTitle")}</p>
                  <p className="text-xs text-muted">
                    {t("updated", { when: format.dateTime(new Date(page.updatedAt), { dateStyle: "medium", timeStyle: "short", timeZone: "Asia/Dhaka" }) })}
                  </p>
                </div>
                <div className="flex flex-wrap gap-2">
                  {page.isPublished && known ? (
                    <Button asChild variant="secondary">
                      <a href={publicPath(page.key as Parameters<typeof publicPath>[0])} target="_blank" rel="noopener noreferrer" data-testid={`page-view-${page.key}`}>
                        <ExternalLink className="size-4" aria-hidden />
                        {t("viewOnShop")}
                        <span className="sr-only"> ({tn(page.key as Parameters<typeof publicPath>[0])})</span>
                      </a>
                    </Button>
                  ) : null}
                  <Button asChild>
                    <Link href={`/admin/content/pages/${page.key}`} data-testid={`page-edit-${page.key}`}>
                      <Pencil className="size-4" aria-hidden />
                      {rights.pageEdit ? t("edit") : t("open")}
                      <span className="sr-only"> ({name})</span>
                    </Link>
                  </Button>
                </div>
              </Card>
            </li>
          );
        })}
      </ul>
    </div>
  );
}
