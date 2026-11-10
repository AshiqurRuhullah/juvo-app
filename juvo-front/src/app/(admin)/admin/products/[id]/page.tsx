import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { ProductEditor } from "@/modules/catalog/product-editor";
import { requirePermission, requireSession } from "@/lib/server/session";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("nav");
  return { title: t("items.products") };
}

export default async function ProductPage({ params, searchParams }: { params: Promise<{ id: string }>; searchParams: Promise<{ tab?: string }> }) {
  const { id } = await params;
  if (!UUID.test(id)) notFound();
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "catalog.product.view", "anywhere");
  const { tab } = await searchParams;
  return <ProductEditor id={id} initialTab={typeof tab === "string" ? tab : undefined} />;
}
