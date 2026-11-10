import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { ProductEditor } from "@/modules/catalog/product-editor";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("catalog.editor");
  return { title: t("newTitle") };
}

export default async function NewProductPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "catalog.product.create", "anywhere");
  return <ProductEditor />;
}
