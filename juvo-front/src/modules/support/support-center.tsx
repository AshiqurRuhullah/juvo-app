"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { Skeleton } from "@/components/ui/surfaces";
import { useSupportPermissions } from "./common";
import { TicketsList } from "./tickets-list";

// The other two tabs are fetched when first opened.
const CannedReplies = dynamic(() => import("./canned-replies"), { ssr: false, loading: () => <Skeleton className="h-48" aria-busy="true" /> });
const ReportView = dynamic(() => import("./report-view"), { ssr: false, loading: () => <Skeleton className="h-64" aria-busy="true" /> });

/** `/admin/support`: Tickets, Canned replies and Report, each tab only for people who hold its permission. */
export function SupportCenter() {
  const t = useTranslations("support.tabs");
  const perms = useSupportPermissions();
  const [tab, setTab] = React.useState("tickets");
  const tabs = [
    { id: "tickets", label: t("tickets"), show: perms.view },
    { id: "canned", label: t("canned"), show: perms.cannedView },
    { id: "report", label: t("report"), show: perms.reportView },
  ].filter((x) => x.show);

  if (tabs.length <= 1) return perms.view ? <TicketsList /> : null;
  return (
    <Tabs tabs={tabs} value={tab} onValueChange={setTab} label={t("label")}>
      {(active) => (active === "tickets" ? <TicketsList /> : active === "canned" ? <CannedReplies /> : <ReportView />)}
    </Tabs>
  );
}
