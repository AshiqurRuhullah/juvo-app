import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { UsersList } from "@/modules/ums/users-list";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("ums.users");
  return { title: t("title") };
}

export default async function UsersPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "ums.user.view", "anywhere");
  const t = await getTranslations("ums.users");
  const tn = await getTranslations("nav");

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("groups.ums") }, { label: tn("items.users") }]} />
      <UsersList />
    </>
  );
}
