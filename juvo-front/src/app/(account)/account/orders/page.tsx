import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { MyOrders } from "@/modules/orders/my-orders";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("orders.mine");
  return { title: t("title") };
}

export default async function OrdersPage() {
  await requireSession({ subject: "customer" });
  const t = await getTranslations("orders.mine");
  return (
    <>
      <PageHeader title={t("title")} description={t("help")} />
      <MyOrders />
    </>
  );
}
