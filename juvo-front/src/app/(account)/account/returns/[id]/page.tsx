import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { requireSession } from "@/lib/server/session";
import { MyReturnDetail } from "@/modules/returns/my-return-detail";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("returns.mineDetail");
  return { title: t("crumb") };
}

export default async function MyReturnPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!UUID.test(id)) notFound();
  await requireSession({ subject: "customer" });
  return <MyReturnDetail id={id} />;
}
