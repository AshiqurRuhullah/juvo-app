import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { InventoryTabs } from "@/modules/inventory/common";
import { CountsList } from "@/modules/inventory/counts-list";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("inventory.counts");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "inventory.count.view", "anywhere");
  const t = await getTranslations("inventory.counts");
  const tn = await getTranslations("nav");

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.inventory"), href: "/admin/inventory" }, { label: t("title") }]} />
      <InventoryTabs />
      <CountsList />
    </>
  );
}
