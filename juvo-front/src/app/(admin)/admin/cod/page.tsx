import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { SettlementsList } from "@/modules/cod/settlements-list";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("cod.list");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "couriers.cod.view", "anywhere");
  return <SettlementsList />;
}
