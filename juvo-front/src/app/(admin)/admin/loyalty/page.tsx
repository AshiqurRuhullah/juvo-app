import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { LoyaltyCenter } from "@/modules/loyalty/loyalty-center";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("loyalty.admin");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "loyalty.points.view", "anywhere");
  const t = await getTranslations("loyalty.admin");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <LoyaltyCenter />
    </>
  );
}
