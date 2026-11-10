import type { Metadata } from "next";
import { Suspense } from "react";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { LookupsView } from "@/modules/catalog/lookups-view";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("nav");
  return { title: t("items.categories") };
}

export default async function CategoriesPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "catalog.category.view", "anywhere");
  const t = await getTranslations("lookups");
  const tn = await getTranslations("nav");

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("groups.catalog") }, { label: tn("items.categories") }]} />
      <Suspense>
        <LookupsView />
      </Suspense>
    </>
  );
}
