"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { usePaymentPermissions } from "@/modules/payments/permissions";
import { ChannelsPanel } from "./channels-panel";
import { LogsPanel } from "./logs-panel";
import { TemplatesPanel } from "./templates-panel";

/** Templates, delivery log and channels. A tab is drawn only when the person holds its permission. */
export function NotificationsView() {
  const t = useTranslations("notifications");
  const perms = usePaymentPermissions();
  const tabs = [
    ...(perms.templateView ? [{ id: "templates", label: t("tabs.templates") }] : []),
    ...(perms.logView ? [{ id: "logs", label: t("tabs.logs") }] : []),
    ...(perms.templateView || perms.channelEdit ? [{ id: "channels", label: t("tabs.channels") }] : []),
  ];
  const [value, setValue] = React.useState(tabs[0]?.id ?? "logs");
  if (tabs.length === 0) return null;
  const current = tabs.some((x) => x.id === value) ? value : tabs[0]!.id;

  return (
    <Tabs tabs={tabs} value={current} onValueChange={setValue} label={t("title")}>
      {(active) => (active === "templates" ? <TemplatesPanel canEdit={perms.templateEdit} /> : active === "logs" ? <LogsPanel canRetry={perms.logRetry} /> : <ChannelsPanel canEdit={perms.channelEdit} />)}
    </Tabs>
  );
}
