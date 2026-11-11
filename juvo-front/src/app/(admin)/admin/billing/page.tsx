import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { canAnywhere } from "@/lib/auth/permissions";
import { requireSession } from "@/lib/server/session";
import { BillingView } from "@/modules/billing/billing-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("billing");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  if (!["tax.invoice.view", "tax.credit-note.view", "tax.rule.view"].some((code) => canAnywhere(me, code))) redirect("/forbidden");
  const t = await getTranslations("billing");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.taxInvoices") }]} />
      <BillingView />
    </>
  );
}
