"use client";

import dynamic from "next/dynamic";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/surfaces";
import { AccountsView } from "./accounts-view";

const DaybookView = dynamic(() => import("./daybook-view").then((m) => m.DaybookView), { loading: () => <Skeleton className="h-64" aria-busy="true" /> });

const IDS = ["accounts", "daybook"] as const;

/** The cash book page: accounts with live balances, and the day book. */
export function CashView() {
  const t = useTranslations("accounting.cashTabs");
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const tabs = IDS.map((id) => ({ id, label: t(id) }));
  const requested = params.get("tab");
  const active = tabs.find((x) => x.id === requested)?.id ?? "accounts";

  const change = (id: string) => {
    const next = new URLSearchParams();
    next.set("tab", id);
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };

  return (
    <Tabs tabs={tabs} value={active} onValueChange={change} label={t("label")}>
      {(id) => (id === "daybook" ? <DaybookView /> : <AccountsView />)}
    </Tabs>
  );
}
