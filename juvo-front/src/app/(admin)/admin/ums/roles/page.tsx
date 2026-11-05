import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { RolesList } from "@/modules/ums/roles-list";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("ums.roles");
  return { title: t("title") };
}

export default async function RolesPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "ums.role.view", "anywhere");
  const t = await getTranslations("ums.roles");
  const tn = await getTranslations("nav");

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("groups.ums") }, { label: tn("items.roles") }]} />
      <RolesList />
    </>
  );
}
