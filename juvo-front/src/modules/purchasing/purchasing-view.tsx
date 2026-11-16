"use client";

import dynamic from "next/dynamic";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/surfaces";
import { OrdersList } from "./orders-list";
import { usePurchasingPermissions } from "./permissions";

const loading = () => <Skeleton className="h-64" aria-busy="true" />;
const SuppliersView = dynamic(() => import("./suppliers-view").then((m) => m.SuppliersView), { loading });
const ReceiptsView = dynamic(() => import("./receipts-view").then((m) => m.ReceiptsView), { loading });
const SuggestionsView = dynamic(() => import("./suggestions-view").then((m) => m.SuggestionsView), { loading });

const IDS = ["orders", "suppliers", "receipts", "suggestions"] as const;
type TabId = (typeof IDS)[number];

/** The purchasing page: purchase orders, suppliers, receipts and suggestions as tabs; a tab appears only when the person may view what is in it. */
export function PurchasingView() {
  const t = useTranslations("purchasing.admin");
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const perms = usePurchasingPermissions();

  const allowed: Record<TabId, boolean> = { orders: perms.poView, suppliers: perms.supplierView, receipts: perms.receiptView, suggestions: perms.poCreate };
  const tabs = IDS.filter((id) => allowed[id]).map((id) => ({ id, label: t(`tabs.${id}`) }));
  const requested = params.get("tab") as TabId | null;
  const active = tabs.find((x) => x.id === requested)?.id ?? tabs[0]?.id ?? "orders";

  const change = (id: string) => {
    const next = new URLSearchParams();
    next.set("tab", id);
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };

  return (
    <Tabs tabs={tabs} value={active} onValueChange={change} label={t("tabsLabel")}>
      {(id) => (id === "suppliers" ? <SuppliersView /> : id === "receipts" ? <ReceiptsView /> : id === "suggestions" ? <SuggestionsView /> : <OrdersList />)}
    </Tabs>
  );
}
