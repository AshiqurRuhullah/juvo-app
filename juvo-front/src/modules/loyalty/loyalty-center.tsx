"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { useLoyaltyPermissions } from "./common";

const loading = () => <Skeleton className="h-48" aria-busy="true" />;
const SettingsView = dynamic(() => import("./settings-view"), { ssr: false, loading });
const GiftCardsView = dynamic(() => import("./gift-cards-view"), { ssr: false, loading });
const ReferralsView = dynamic(() => import("./referrals-view"), { ssr: false, loading });
const CustomerLookup = dynamic(() => import("./customer-lookup"), { ssr: false, loading });

/** `/admin/loyalty`: Settings, Gift cards, Referrals and Customer rewards, each tab only for people who hold its permission. */
export function LoyaltyCenter() {
  const t = useTranslations("loyalty.tabs");
  const perms = useLoyaltyPermissions();
  const tabs = [
    { id: "settings", label: t("settings"), show: perms.pointsView },
    { id: "cards", label: t("cards"), show: perms.cardView },
    { id: "referrals", label: t("referrals"), show: perms.referralView },
    { id: "customers", label: t("customers"), show: perms.pointsView },
  ].filter((x) => x.show);
  const [tab, setTab] = React.useState<string | null>(null);
  const active = tabs.find((x) => x.id === tab)?.id ?? tabs[0]?.id;

  if (!active) return <Alert variant="info">{t("none")}</Alert>;
  return (
    <Tabs tabs={tabs} value={active} onValueChange={setTab} label={t("label")}>
      {(id) => (id === "settings" ? <SettingsView /> : id === "cards" ? <GiftCardsView /> : id === "referrals" ? <ReferralsView /> : <CustomerLookup />)}
    </Tabs>
  );
}
