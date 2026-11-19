import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { NewZone } from "@/modules/delivery/zone-editor";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("delivery.editor");
  return { title: t("newTitle") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "delivery.zone.create", "anywhere");
  return <NewZone />;
}
