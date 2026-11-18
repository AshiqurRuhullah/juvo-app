import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { PromotionEditor } from "@/modules/promotions/promotion-editor";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("promotions.editor");
  return { title: t("newTitle") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "promotions.promotion.create", "anywhere");
  return <PromotionEditor />;
}
