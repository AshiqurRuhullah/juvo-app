import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { requirePermission, requireSession } from "@/lib/server/session";
import { ExpenseDetail } from "@/modules/accounting/expense-detail";
import { UUID_RE } from "@/modules/cod/ids";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("accounting.detail");
  return { title: t("pageTitle") };
}

export default async function Page({ params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  if (!UUID_RE.test(id)) notFound();
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "accounting.expense.view", "anywhere");
  return <ExpenseDetail id={id} />;
}
