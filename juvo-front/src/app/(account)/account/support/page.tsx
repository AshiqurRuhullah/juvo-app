import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { MyTicketsList } from "@/modules/support/my-tickets-list";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("support.mine");
  return { title: t("title") };
}

export default async function MySupportPage() {
  await requireSession({ subject: "customer" });
  const t = await getTranslations("support.mine");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.accountHome"), href: "/account" }, { label: t("title") }]} />
      <MyTicketsList />
    </>
  );
}
