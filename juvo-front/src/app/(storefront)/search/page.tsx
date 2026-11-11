import type { Metadata } from "next";
import { Suspense } from "react";
import { getTranslations } from "next-intl/server";
import { loadListing, type RawSearchParams } from "@/lib/server/store-listing";
import { Breadcrumbs, PageHeading } from "@/modules/store/page-heading";
import { ProductListing } from "@/modules/store/product-listing";

export async function generateMetadata({ searchParams }: { searchParams: Promise<RawSearchParams> }): Promise<Metadata> {
  const t = await getTranslations("store.search");
  const raw = (await searchParams).q;
  const q = (Array.isArray(raw) ? raw[0] : raw)?.trim();
  return { title: q ? t("pageTitleFor", { q }) : t("pageTitle"), robots: { index: false } };
}

/** Search results: the same grid as the shop, narrowed by the text in ?q= (small spelling mistakes are tolerated by the API). */
export default async function SearchPage({ searchParams }: { searchParams: Promise<RawSearchParams> }) {
  const t = await getTranslations("store.search");
  const b = await getTranslations("store.breadcrumb");
  const { query, page, facets } = await loadListing(undefined, await searchParams);
  return (
    <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6">
      <Breadcrumbs label={b("label")} items={[{ label: b("home"), href: "/" }, { label: t("pageTitle") }]} />
      <PageHeading title={query.q ? t("resultsFor", { q: query.q }) : t("pageTitle")} subtitle={query.q ? undefined : t("typeToSearch")} />
      <Suspense>
        <ProductListing searchPage initialQuery={query} initialPage={page} initialFacets={facets} />
      </Suspense>
    </div>
  );
}
