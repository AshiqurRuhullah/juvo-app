import type { Metadata } from "next";
import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { buttonVariants } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/surfaces";
import { requireSession } from "@/lib/server/session";
import { RequestFlow } from "@/modules/returns/request-flow";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("returns.request");
  return { title: t("title") };
}

/** `/account/returns/new?order=<orderId>`: the return or exchange request for one order (the order page links here). */
export default async function NewReturnPage({ searchParams }: { searchParams: Promise<{ order?: string | string[] }> }) {
  await requireSession({ subject: "customer" });
  const { order } = await searchParams;
  const orderId = Array.isArray(order) ? order[0] : order;
  const t = await getTranslations("returns.request");
  const tn = await getTranslations("nav");
  const crumbs = [
    { label: tn("items.accountHome"), href: "/account" },
    { label: tn("items.returns"), href: "/account/returns" },
    { label: t("crumb") },
  ];

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={crumbs} />
      {orderId && UUID.test(orderId) ? (
        <RequestFlow orderId={orderId} />
      ) : (
        <EmptyState
          title={t("noOrder")}
          description={t("noOrderHelp")}
          action={
            <Link href="/account/returns" className={buttonVariants({ variant: "secondary" })}>
              {t("myReturns")}
            </Link>
          }
        />
      )}
    </>
  );
}
