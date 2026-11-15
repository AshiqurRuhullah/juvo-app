"use client";

import { useTranslations } from "next-intl";
import { Radio, WifiOff } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import type { LiveStatus } from "@/lib/realtime/use-live";

/** Small connection indicator for screens that follow the live hub. Text carries the meaning; the dot only decorates. */
export function LiveBadge({ status, className }: { status: LiveStatus; className?: string }) {
  const t = useTranslations("live");
  if (status === "off") return null;
  const offline = status === "offline";
  return (
    <Badge variant={status === "live" ? "success" : offline ? "danger" : "warning"} className={className} data-testid="live-badge" data-live={status} title={t("label")}>
      {offline ? <WifiOff className="size-3.5" aria-hidden /> : <Radio className="size-3.5" aria-hidden />}
      {t(status)}
    </Badge>
  );
}
