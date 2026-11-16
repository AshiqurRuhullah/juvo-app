import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { MyTicketNew } from "@/modules/support/my-ticket-new";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("support.new");
  return { title: t("title") };
}

/** `/account/support/new?order=<orderId>`: a new request, optionally about one order (the order page links here). */
export default async function NewTicketPage({ searchParams }: { searchParams: Promise<{ order?: string | string[] }> }) {
  await requireSession({ subject: "customer" });
  const { order } = await searchParams;
  const raw = Array.isArray(order) ? order[0] : order;
  const orderId = raw && UUID.test(raw) ? raw : undefined;
  const t = await getTranslations("support.new");
  const tn = await getTranslations("nav");
  const crumbs = [
    { label: tn("items.accountHome"), href: "/account" },
    { label: tn("items.mySupport"), href: "/account/support" },
    { label: t("crumb") },
  ];
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={crumbs} />
      <MyTicketNew orderId={orderId} />
    </>
  );
}
