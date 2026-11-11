import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { BulkSlipsView } from "@/modules/orders/packing-slip";
import { requirePermission, requireSession } from "@/lib/server/session";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("orders.slip");
  return { title: t("bulkTitle") };
}

export default async function Page({ searchParams }: { searchParams: Promise<{ ids?: string | string[] }> }) {
  const { ids } = await searchParams;
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "orders.order.print", "anywhere");
  const list = (Array.isArray(ids) ? ids.join(",") : (ids ?? ""))
    .split(",")
    .map((x) => x.trim())
    .filter((x) => UUID.test(x))
    .slice(0, 100);
  return <BulkSlipsView ids={list} />;
}
