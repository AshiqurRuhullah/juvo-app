import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { Suspense } from "react";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { Skeleton } from "@/components/ui/surfaces";
import { requirePermission, requireSession } from "@/lib/server/session";
import { isReportName, viewPermission } from "@/modules/reports/logic";
import { ReportSwitch } from "@/modules/reports/report-switch";

type Props = { params: Promise<{ report: string }> };

export async function generateMetadata({ params }: Props): Promise<Metadata> {
  const { report } = await params;
  const t = await getTranslations("reports.names");
  return { title: isReportName(report) ? t(report) : t("title") };
}

export default async function Page({ params }: Props) {
  const { report } = await params;
  if (!isReportName(report)) notFound();
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, viewPermission(report), "anywhere");
  const t = await getTranslations("reports.names");
  const td = await getTranslations("reports.purpose");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t(report)} description={td(report)} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title"), href: "/admin/reports" }, { label: t(report) }]} />
      <Suspense fallback={<Skeleton className="h-96" />}>
        <ReportSwitch report={report} />
      </Suspense>
    </>
  );
}
