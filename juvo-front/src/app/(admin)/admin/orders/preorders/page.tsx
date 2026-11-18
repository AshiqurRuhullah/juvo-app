import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { PreorderQueue } from "@/modules/preorders/preorder-queue";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("preorders.queue");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "orders.order.view", "anywhere");
  const t = await getTranslations("preorders.queue");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader
        title={t("title")}
        description={t("subtitle")}
        crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.orders"), href: "/admin/orders" }, { label: t("title") }]}
      />
      <PreorderQueue />
    </>
  );
}
