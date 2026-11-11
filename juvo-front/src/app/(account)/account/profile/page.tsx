import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { ProfileView } from "@/modules/myaccount/profile-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("myaccount.profile");
  return { title: t("title") };
}

export default async function ProfilePage() {
  await requireSession({ subject: "customer" });
  const t = await getTranslations("myaccount.profile");
  const home = await getTranslations("nav.items");
  return (
    <>
      <PageHeader title={t("title")} description={t("help")} crumbs={[{ label: home("accountHome"), href: "/account" }, { label: t("title") }]} />
      <ProfileView />
    </>
  );
}
