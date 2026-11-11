"use client";

import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { usePaymentPermissions } from "@/modules/payments/permissions";
import { CreditNotesList, InvoicesList } from "./invoices-list";
import { TaxRules } from "./tax-rules";

const IDS = ["invoices", "creditNotes", "rules"] as const;
type TabId = (typeof IDS)[number];

/** The billing page: invoices, credit notes and tax rules as tabs. A tab appears only when the person may view what is in it. */
export function BillingView() {
  const t = useTranslations("billing");
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const perms = usePaymentPermissions();

  const allowed: Record<TabId, boolean> = { invoices: perms.invoiceView, creditNotes: perms.creditNoteView, rules: perms.taxRuleView };
  const tabs = IDS.filter((id) => allowed[id]).map((id) => ({ id, label: t(`tabs.${id}`) }));
  const requested = params.get("tab") as TabId | null;
  const active = tabs.find((x) => x.id === requested)?.id ?? tabs[0]?.id ?? "invoices";

  const change = (id: string) => {
    const next = new URLSearchParams(params.toString());
    next.set("tab", id);
    router.replace(`${pathname}?${next.toString()}`, { scroll: false });
  };

  return (
    <Tabs tabs={tabs} value={active} onValueChange={change} label={t("tabsLabel")}>
      {(id) => (id === "rules" ? <TaxRules /> : id === "creditNotes" ? <CreditNotesList /> : <InvoicesList />)}
    </Tabs>
  );
}
