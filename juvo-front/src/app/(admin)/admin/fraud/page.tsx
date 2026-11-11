import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { FraudView } from "@/modules/orders/fraud-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("orders.fraud");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "fraud.flag.view", "anywhere");
  const t = await getTranslations("orders.fraud");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <FraudView />
    </>
  );
}
