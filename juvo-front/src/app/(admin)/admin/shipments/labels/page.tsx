import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { parseIds } from "@/modules/couriers/admin-logic";
import { LabelView } from "@/modules/couriers/label-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("shipments.label");
  return { title: t("bulkTitle") };
}

export default async function Page({ searchParams }: { searchParams: Promise<{ ids?: string | string[] }> }) {
  const { ids } = await searchParams;
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "couriers.shipment.print-label", "anywhere");
  return <LabelView ids={parseIds(ids, 50)} />;
}
