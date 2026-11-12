"use client";

import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { LedgerView } from "./ledger-view";
import { usePaymentPermissions } from "./permissions";
import { PaymentsList } from "./payments-list";
import { RefundsList } from "./refunds-list";
import { PaymentSettingsView } from "./settings-view";

const IDS = ["payments", "refunds", "ledger", "settings"] as const;
type TabId = (typeof IDS)[number];

/** The payments page: payments, refunds, the ledger and the settings as tabs; a tab appears only when the person may view what is in it. */
export function AdminPayments() {
  const t = useTranslations("payments.admin");
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const perms = usePaymentPermissions();

  const allowed: Record<TabId, boolean> = { payments: perms.view, refunds: perms.refundView, ledger: perms.ledger, settings: perms.gatewayView };
  const tabs = IDS.filter((id) => allowed[id]).map((id) => ({ id, label: t(`tabs.${id}`) }));
  const requested = params.get("tab") as TabId | null;
  const active = tabs.find((x) => x.id === requested)?.id ?? tabs[0]?.id ?? "payments";

  const change = (id: string) => {
    const next = new URLSearchParams(params.toString());
    next.set("tab", id);
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };

  return (
    <Tabs tabs={tabs} value={active} onValueChange={change} label={t("tabsLabel")}>
      {(id) => (id === "refunds" ? <RefundsList /> : id === "ledger" ? <LedgerView /> : id === "settings" ? <PaymentSettingsView /> : <PaymentsList />)}
    </Tabs>
  );
}
