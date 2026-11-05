import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { SignInViewer } from "@/modules/ums/sign-in-viewer";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("ums.signIns");
  return { title: t("pageTitle") };
}

export default async function SignInsPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "ums.sign-in.view", "anywhere");
  const t = await getTranslations("ums.signIns");
  const tn = await getTranslations("nav");

  return (
    <>
      <PageHeader title={t("pageTitle")} description={t("pageSubtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("groups.ums") }, { label: tn("items.signIns") }]} />
      <SignInViewer />
    </>
  );
}
