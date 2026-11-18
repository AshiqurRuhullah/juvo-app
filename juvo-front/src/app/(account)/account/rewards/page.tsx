import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { RewardsView } from "@/modules/loyalty/rewards-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("loyalty.me");
  return { title: t("title") };
}

export default async function RewardsPage() {
  await requireSession({ subject: "customer" });
  const t = await getTranslations("loyalty.me");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} />
      <RewardsView />
    </>
  );
}
