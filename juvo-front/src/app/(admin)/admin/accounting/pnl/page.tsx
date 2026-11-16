import type { Metadata } from "next";
import { Suspense } from "react";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { Skeleton } from "@/components/ui/surfaces";
import { requirePermission, requireSession } from "@/lib/server/session";
import { PnlView } from "@/modules/reports/pnl-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("reports.pnl");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "accounting.pnl.view", "anywhere");
  const t = await getTranslations("reports.pnl");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <Suspense fallback={<Skeleton className="h-96" />}>
        <PnlView />
      </Suspense>
    </>
  );
}
