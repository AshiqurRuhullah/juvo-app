import type { Metadata } from "next";
import { Suspense } from "react";
import { getTranslations } from "next-intl/server";
import { loadListing, type RawSearchParams } from "@/lib/server/store-listing";
import { Breadcrumbs, PageHeading } from "@/modules/store/page-heading";
import { ProductListing } from "@/modules/store/product-listing";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("store.shop");
  return { title: t("title"), description: t("description") };
}

/** Every published product, with filters and sorting. */
export default async function ShopPage({ searchParams }: { searchParams: Promise<RawSearchParams> }) {
  const t = await getTranslations("store.shop");
  const b = await getTranslations("store.breadcrumb");
  const { query, page, facets } = await loadListing(undefined, await searchParams);
  return (
    <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6">
      <Breadcrumbs label={b("label")} items={[{ label: b("home"), href: "/" }, { label: t("title") }]} />
      <PageHeading title={t("title")} subtitle={t("description")} />
      <Suspense>
        <ProductListing initialQuery={query} initialPage={page} initialFacets={facets} />
      </Suspense>
    </div>
  );
}
