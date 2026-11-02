"use client";

import { useTranslations } from "next-intl";
import { Bell, BellRing } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/overlays";
import { EmptyState } from "@/components/ui/surfaces";

/**
 * Notification centre. Live alerts (new order, low stock, failed booking) arrive over SignalR from Step 8; until then it honestly
 * shows that there is nothing new.
 */
export function NotificationsBell() {
  const t = useTranslations("shell");
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="icon" aria-label={t("notifications")}>
          <Bell aria-hidden />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-[min(22rem,calc(100vw-1.5rem))] p-2">
        <EmptyState icon={<BellRing aria-hidden />} title={t("noNotifications")} description={t("noNotificationsBody")} className="py-8" />
      </PopoverContent>
    </Popover>
  );
}
