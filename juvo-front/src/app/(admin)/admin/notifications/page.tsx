import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { canAnywhere } from "@/lib/auth/permissions";
import { requireSession } from "@/lib/server/session";
import { NotificationsView } from "@/modules/notifications/notifications-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("notifications");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  if (!["notifications.template.view", "notifications.log.view", "notifications.channel.edit"].some((code) => canAnywhere(me, code))) redirect("/forbidden");
  const t = await getTranslations("notifications");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <NotificationsView />
    </>
  );
}
