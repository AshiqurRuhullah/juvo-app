import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { ExpensesView } from "@/modules/accounting/expenses-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("accounting.expenses");
  return { title: t("title") };
}

export default async function Page() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "accounting.expense.view", "anywhere");
  const t = await getTranslations("accounting.expenses");
  const tn = await getTranslations("nav");
  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />
      <ExpensesView />
    </>
  );
}
