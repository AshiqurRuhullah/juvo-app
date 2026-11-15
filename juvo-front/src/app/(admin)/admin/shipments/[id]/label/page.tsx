import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { LabelView } from "@/modules/couriers/label-view";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("shipments.label");
  return { title: t("title") };
}

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!UUID.test(id)) notFound();
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "couriers.shipment.print-label", "anywhere");
  return <LabelView ids={[id]} single />;
}
