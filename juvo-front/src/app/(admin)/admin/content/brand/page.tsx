import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { BrandView } from "@/modules/content/brand-form";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("content.brand");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "content.brand.view", "anywhere");
  const t = await getTranslations("content.brand");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <BrandView />
    </>
  );
}
