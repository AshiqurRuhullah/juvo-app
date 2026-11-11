import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { AddressesView } from "@/modules/myaccount/addresses-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("myaccount.addresses");
  return { title: t("title") };
}

export default async function AddressesPage() {
  await requireSession({ subject: "customer" });
  const t = await getTranslations("myaccount.addresses");
  return (
    <>
      <PageHeader title={t("title")} description={t("help")} />
      <AddressesView />
    </>
  );
}
