import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { PurchasingView } from "@/modules/purchasing/purchasing-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("purchasing.admin");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "purchasing.po.view", "anywhere");
  const t = await getTranslations("purchasing.admin");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <PurchasingView />
    </>
  );
}
