import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { OrderEditor } from "@/modules/purchasing/order-editor";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("purchasing.editor");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "purchasing.po.create", "anywhere");
  return <OrderEditor />;
}
