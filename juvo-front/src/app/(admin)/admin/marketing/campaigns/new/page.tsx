import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { NewCampaignView } from "@/modules/marketing/campaign-detail";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("marketing.campaigns");
  return { title: t("newTitle") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "marketing.campaign.create", "anywhere");
  const t = await getTranslations("marketing.campaigns");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("newTitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title"), href: "/admin/marketing/campaigns" }, { label: t("newTitle") }]} />
      <NewCampaignView />
    </>
  );
}
