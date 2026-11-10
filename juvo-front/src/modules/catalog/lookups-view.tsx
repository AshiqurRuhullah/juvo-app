"use client";

import * as React from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { usePermissions } from "@/components/providers/session-provider";
import { BrandsManager } from "./brands-manager";
import { CategoriesManager } from "./categories-manager";
import { SizeChartsManager } from "./size-charts-manager";

const IDS = ["categories", "brands", "sizeCharts"] as const;
type TabId = (typeof IDS)[number];

/** The three catalog lists as tabs; the chosen tab is in the address (?tab=brands) so a reload or a shared link keeps it. */
export function LookupsView() {
  const t = useTranslations("lookups");
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const { can } = usePermissions();

  const allowed: Record<TabId, boolean> = {
    categories: can("catalog.category.view", "anywhere"),
    brands: can("catalog.brand.view", "anywhere"),
    sizeCharts: can("catalog.size-chart.view", "anywhere"),
  };
  const tabs = IDS.filter((id) => allowed[id]).map((id) => ({ id, label: t(`tabs.${id}`) }));
  const requested = params.get("tab") as TabId | null;
  const active = tabs.find((x) => x.id === requested)?.id ?? tabs[0]?.id ?? "categories";

  const change = (id: string) => {
    const next = new URLSearchParams(params.toString());
    next.set("tab", id);
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };

  return (
    <Tabs tabs={tabs} value={active} onValueChange={change} label={t("tabsLabel")}>
      {(id) => (id === "brands" ? <BrandsManager /> : id === "sizeCharts" ? <SizeChartsManager /> : <CategoriesManager />)}
    </Tabs>
  );
}
