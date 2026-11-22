import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { OrdersList } from "@/modules/orders/orders-list";
import { isOrderStatus } from "@/modules/dashboard/period";
import { isIsoDate } from "@/modules/reports/logic";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("orders.list");
  return { title: t("title") };
}

export default async function Page({ searchParams }: { searchParams: Promise<{ status?: string; from?: string; to?: string }> }) {
  const { status, from, to } = await searchParams;
  const days = isIsoDate(from) && isIsoDate(to) && from <= to ? { from, to } : null;
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "orders.order.view", "anywhere");
  const t = await getTranslations("orders.list");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <OrdersList key={`${status ?? "all"}|${days?.from ?? ""}|${days?.to ?? ""}`} initialStatus={isOrderStatus(status) ? status : undefined} initialFrom={days?.from} initialTo={days?.to} />
    </>
  );
}
