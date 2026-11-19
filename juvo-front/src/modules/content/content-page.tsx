import type { Metadata } from "next";
import Link from "next/link";
import { notFound } from "next/navigation";
import { getFormatter, getLocale, getTranslations } from "next-intl/server";
import { FileX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/surfaces";
import { pickText, type PageKey } from "./logic";
import { Markdown } from "./markdown-view";
import { getPublicPage } from "./public";

/** Tab title and search description from the page's own meta fields (title when there is no meta title). Nothing invented. */
export async function contentMetadata(key: PageKey): Promise<Metadata> {
  const [page, locale, t] = await Promise.all([getPublicPage(key), getLocale(), getTranslations("content.pageNames")]);
  if (!page) return { title: t(key), robots: { index: false } };
  const title = pickText(page.metaTitle, locale) ?? pickText(page.title, locale) ?? t(key);
  const description = pickText(page.metaDescription, locale);
  return { title, ...(description ? { description } : {}) };
}

/** A published page of the shop. An unpublished, emptied or unknown page is a real 404 with a plain "not available yet" message. */
export async function ContentPageView({ pageKey }: { pageKey: PageKey }) {
  const [page, locale, format] = await Promise.all([getPublicPage(pageKey), getLocale(), getFormatter()]);
  const body = page ? pickText(page.body, locale) : null;
  if (!page || !body) notFound();
  // Bengali text is used only when the Bengali body exists, so the title and the text are always in the same language.
  const bengali = locale === "bn" && !!page.body.bn?.trim();
  const title = (bengali ? page.title.bn?.trim() || page.title.en : page.title.en) ?? "";
  const t = await getTranslations("content.public");
  return (
    <article className="mx-auto w-full max-w-3xl px-4 py-8 sm:px-6 sm:py-12" lang={bengali ? "bn" : "en"} data-testid="content-page" data-page={pageKey}>
      {title ? <h1 className="mb-6 font-display text-3xl font-semibold text-strong sm:text-4xl [overflow-wrap:anywhere]">{title}</h1> : null}
      <Markdown source={bengali ? (page.body.bn as string) : body} />
      <p className="mt-10 border-t border-line pt-4 text-sm text-muted">
        {t("updated", { when: format.dateTime(new Date(page.updatedAt), { dateStyle: "medium", timeZone: "Asia/Dhaka" }) })}
      </p>
    </article>
  );
}

/** What a customer sees for a page that is not published (shown by each page's not-found.tsx). */
export async function ContentNotAvailable() {
  const t = await getTranslations("content.public");
  return (
    <div className="mx-auto max-w-2xl px-4 py-16" data-testid="content-not-available">
      <EmptyState
        icon={<FileX aria-hidden />}
        title={t("notAvailableTitle")}
        description={t("notAvailableBody")}
        action={
          <Button asChild>
            <Link href="/">{t("backHome")}</Link>
          </Button>
        }
      />
    </div>
  );
}
