"use client";

import * as React from "react";
import { useLocale, useTranslations } from "next-intl";
import { CircleCheck, CircleX, Globe, MapPinned, Undo2 } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import type { DeliveryRights, ZoneKind } from "./logic";

/** What this staff member may do with delivery zones (all global permissions). Display only: the API still refuses. */
export function useDeliveryRights(): DeliveryRights {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      view: canAnywhere(me, "delivery.zone.view"),
      create: canAnywhere(me, "delivery.zone.create"),
      edit: canAnywhere(me, "delivery.zone.edit"),
      delete: canAnywhere(me, "delivery.zone.delete"),
      rateEdit: canAnywhere(me, "delivery.rate.edit"),
      countryView: canAnywhere(me, "delivery.country.view"),
      countryEnable: canAnywhere(me, "delivery.country.enable"),
    }),
    [me],
  );
}

/** Active or switched off, always an icon and a word. */
export function ActiveBadge({ isActive }: { isActive: boolean }) {
  const t = useTranslations("delivery.status");
  const Icon = isActive ? CircleCheck : CircleX;
  return (
    <Badge variant={isActive ? "success" : "outline"} data-testid="zone-status">
      <Icon className="size-3.5" aria-hidden />
      {isActive ? t("active") : t("inactive")}
    </Badge>
  );
}

export function KindBadge({ kind }: { kind: ZoneKind }) {
  const t = useTranslations("delivery.kind");
  const Icon = kind === "Domestic" ? MapPinned : Globe;
  return (
    <Badge variant="info">
      <Icon className="size-3.5" aria-hidden />
      {t(kind)}
    </Badge>
  );
}

export function FallbackBadge() {
  const t = useTranslations("delivery.status");
  return (
    <Badge variant="brand" data-testid="zone-fallback">
      <Undo2 className="size-3.5" aria-hidden />
      {t("fallback")}
    </Badge>
  );
}

/** English or Bengali name of a place, by the current language (falls back to English). */
export function useLocalName() {
  const locale = useLocale();
  return (item: { nameEn: string; nameBn: string | null }) => (locale === "bn" && item.nameBn ? item.nameBn : item.nameEn);
}

/** The API's field messages (validation problems) as plain lines, never raw server text beyond the field messages it wrote for staff. */
export function fieldMessages(problem: { errors?: Record<string, string[]> } | null | undefined): string[] {
  return Object.values(problem?.errors ?? {}).flat();
}
