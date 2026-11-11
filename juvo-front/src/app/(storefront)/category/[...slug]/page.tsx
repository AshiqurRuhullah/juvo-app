import type { Metadata } from "next";
import Link from "next/link";
import { Suspense } from "react";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { storeGet } from "@/lib/server/store";
import { findCategoryPath, loadListing, type RawSearchParams } from "@/lib/server/store-listing";
import type { StoreCategory } from "@/modules/store/api";
import { Breadcrumbs, PageHeading } from "@/modules/store/page-heading";
import { ProductListing } from "@/modules/store/product-listing";

type Params = { slug: string[] };

/** The category in the URL is its last segment, so /category/women and /category/women/dresses both work. */
async function resolve(params: Promise<Params>) {
  const { slug } = await params;
  const tree = (await storeGet<StoreCategory[]>("/categories")) ?? [];
  const path = findCategoryPath(tree, decodeURIComponent(slug[slug.length - 1]));
  return path;
}

export async function generateMetadata({ params }: { params: Promise<Params> }): Promise<Metadata> {
  const path = await resolve(params);
  const locale = await getLocale();
  const t = await getTranslations("store.category");
  const category = path?.[path.length - 1];
  if (!category) return { title: t("notFound") };
  const name = locale === "bn" && category.nameBn ? category.nameBn : category.nameEn;
  return { title: name, description: t("description", { name }) };
}

/** A category's products (its sub-categories' too), with filters and sorting. */
export default async function CategoryPage({ params, searchParams }: { params: Promise<Params>; searchParams: Promise<RawSearchParams> }) {
  const path = await resolve(params);
  if (!path) notFound();
  const category = path[path.length - 1];
  const locale = await getLocale();
  const t = await getTranslations("store.category");
  const b = await getTranslations("store.breadcrumb");
  const name = (c: StoreCategory) => (locale === "bn" && c.nameBn ? c.nameBn : c.nameEn);
  const { query, page, facets } = await loadListing(category.slug, await searchParams);

  const crumbs = [
    { label: b("home"), href: "/" },
    ...path.map((node, index) => ({ label: name(node), href: index === path.length - 1 ? undefined : `/category/${node.slug}` })),
  ];

  return (
    <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6">
      <Breadcrumbs label={b("label")} items={crumbs} />
      <PageHeading title={name(category)} subtitle={t("description", { name: name(category) })} />
      {category.children.length > 0 ? (
        <ul className="-mt-2 mb-6 flex flex-wrap gap-2" aria-label={t("subcategories")}>
          {category.children.map((child) => (
            <li key={child.id}>
              <Link href={`/category/${child.slug}`} className="inline-flex min-h-11 items-center rounded-full border border-line bg-card px-4 text-sm font-medium text-strong hover:bg-sunken">
                {name(child)}
              </Link>
            </li>
          ))}
        </ul>
      ) : null}
      <Suspense>
        <ProductListing category={category.slug} initialQuery={query} initialPage={page} initialFacets={facets} />
      </Suspense>
    </div>
  );
}
