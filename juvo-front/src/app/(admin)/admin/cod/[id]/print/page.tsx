import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { UUID_RE } from "@/modules/cod/ids";
import { SettlementPrint } from "@/modules/cod/print-sheet";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("cod.print");
  return { title: t("title") };
}

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!UUID_RE.test(id)) notFound();
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "couriers.cod.view", "anywhere");
  return <SettlementPrint id={id} />;
}
