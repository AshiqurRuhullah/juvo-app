import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { NewSettlement } from "@/modules/cod/new-settlement";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("cod.new");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "couriers.cod.settle", "anywhere");
  return <NewSettlement />;
}
