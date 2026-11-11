import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { OrderDetail } from "@/modules/orders/order-detail";
import { requirePermission, requireSession } from "@/lib/server/session";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("orders.detail");
  return { title: t("title") };
}

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!UUID.test(id)) notFound();
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "orders.order.view", "anywhere");
  return <OrderDetail id={id} />;
}
