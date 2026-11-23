import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { HealthView } from "@/modules/system/health-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("sysops.health");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "system.health.view", "anywhere");
  const t = await getTranslations("sysops.health");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <HealthView />
    </>
  );
}
