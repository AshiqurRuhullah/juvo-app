"use client";

import dynamic from "next/dynamic";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/surfaces";
import { ExpensesList } from "./expenses-list";

const loading = () => <Skeleton className="h-64" aria-busy="true" />;
const CategoriesView = dynamic(() => import("./categories-view").then((m) => m.CategoriesView), { loading });
const RecurringView = dynamic(() => import("./recurring-view").then((m) => m.RecurringView), { loading });

const IDS = ["expenses", "categories", "recurring"] as const;
type TabId = (typeof IDS)[number];

/** The expenses page: expenses, categories and repeating expenses as tabs. */
export function ExpensesView() {
  const t = useTranslations("accounting.expenseTabs");
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const tabs = IDS.map((id) => ({ id, label: t(id) }));
  const requested = params.get("tab") as TabId | null;
  const active = tabs.find((x) => x.id === requested)?.id ?? "expenses";

  const change = (id: string) => {
    const next = new URLSearchParams();
    next.set("tab", id);
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };

  return (
    <Tabs tabs={tabs} value={active} onValueChange={change} label={t("label")}>
      {(id) => (id === "categories" ? <CategoriesView /> : id === "recurring" ? <RecurringView /> : <ExpensesList />)}
    </Tabs>
  );
}
