import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { MyReturnsList } from "@/modules/returns/my-returns-list";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("returns.mine");
  return { title: t("title") };
}

export default async function MyReturnsPage() {
  await requireSession({ subject: "customer" });
  const t = await getTranslations("returns.mine");
  return (
    <>
      <PageHeader title={t("title")} description={t("help")} />
      <MyReturnsList />
    </>
  );
}
