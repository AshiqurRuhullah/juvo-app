import type { Metadata } from "next";
import Link from "next/link";
import { ArrowRight, Boxes, ChartNoAxesColumn, ClipboardList, Landmark, TrendingUp, Truck, Users, Wallet } from "lucide-react";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { Card, CardDescription, CardHeader, CardTitle, EmptyState } from "@/components/ui/surfaces";
import { canAnywhere } from "@/lib/auth/permissions";
import { requireSession } from "@/lib/server/session";
import { openableReports, type ReportName } from "@/modules/reports/logic";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("reports.names");
  return { title: t("title") };
}

const ICON: Record<ReportName, React.ComponentType<{ className?: string }>> = {
  sales: ChartNoAxesColumn,
  orders: ClipboardList,
  inventory: Boxes,
  finance: Wallet,
  courier: Truck,
  customers: Users,
};

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  const t = await getTranslations("reports.names");
  const td = await getTranslations("reports.purpose");
  const tl = await getTranslations("reports.landing");
  const tn = await getTranslations("nav");
  const reports = openableReports((code) => canAnywhere(me, code));
  const pnl = canAnywhere(me, "accounting.pnl.view");
  const cards = [
    ...reports.map((r) => ({ key: r, href: `/admin/reports/${r}`, title: t(r), text: td(r), Icon: ICON[r] })),
    ...(pnl ? [{ key: "pnl", href: "/admin/accounting/pnl", title: tn("items.profitLoss"), text: td("pnl"), Icon: TrendingUp }] : []),
  ];
  return (
    <>
      <PageHeader title={t("title")} description={tl("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      {cards.length === 0 ? (
        <Card>
          <EmptyState icon={<Landmark />} title={tl("emptyTitle")} description={tl("emptyText")} />
        </Card>
      ) : (
        <ul className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3" data-testid="report-cards">
          {cards.map(({ key, href, title, text, Icon }) => (
            <li key={key}>
              <Link href={href} data-testid={`report-card-${key}`} className="block h-full rounded-card focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-[var(--brand-primary-glow)]">
                <Card interactive className="h-full">
                  <CardHeader>
                    <CardTitle className="flex items-center gap-3">
                      <span className="flex size-10 shrink-0 items-center justify-center rounded-control bg-primary-soft text-primary-dark" aria-hidden>
                        <Icon className="size-5" />
                      </span>
                      <span className="min-w-0 flex-1">{title}</span>
                      <ArrowRight className="size-5 shrink-0 text-muted rtl:rotate-180" aria-hidden />
                    </CardTitle>
                    <CardDescription>{text}</CardDescription>
                  </CardHeader>
                </Card>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
