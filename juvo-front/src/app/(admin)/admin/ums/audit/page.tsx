import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { AuditViewer } from "@/modules/ums/audit-viewer";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("ums.audit");
  return { title: t("title") };
}

export default async function AuditPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "ums.audit.view", "anywhere");
  const t = await getTranslations("ums.audit");
  const tn = await getTranslations("nav");

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("groups.ums") }, { label: tn("items.audit") }]} />
      <AuditViewer />
    </>
  );
}
