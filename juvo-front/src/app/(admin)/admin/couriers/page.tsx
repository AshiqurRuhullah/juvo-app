import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { CouriersView } from "@/modules/couriers/couriers-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("couriers");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "couriers.courier.view", "anywhere");
  const t = await getTranslations("couriers");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <CouriersView />
    </>
  );
}
