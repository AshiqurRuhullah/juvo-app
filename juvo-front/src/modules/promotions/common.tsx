"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Archive, CalendarClock, CircleCheck, CircleDashed, CircleStop } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import type { SelectOption } from "@/components/ui/select";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import { PROMOTION_STATUSES, PROMOTION_TYPES, statusTone, type PromotionPerms, type PromotionStatus } from "./logic";

/** What this staff member may do with promotions (they are global, not per branch). Display only: the API still refuses. */
export function usePromotionPermissions(): PromotionPerms {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      view: canAnywhere(me, "promotions.promotion.view"),
      create: canAnywhere(me, "promotions.promotion.create"),
      edit: canAnywhere(me, "promotions.promotion.edit"),
      remove: canAnywhere(me, "promotions.promotion.delete"),
    }),
    [me],
  );
}

const ICON: Record<PromotionStatus, React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>> = {
  Draft: CircleDashed,
  Scheduled: CalendarClock,
  Active: CircleCheck,
  Ended: CircleStop,
  Archived: Archive,
};

/** Status as a chip with a word and an icon, so colour is never the only signal. */
export function StatusBadge({ status }: { status: PromotionStatus }) {
  const t = useTranslations("promotions.status");
  const Icon = ICON[status];
  return (
    <Badge variant={statusTone(status)} data-testid="promotion-status" data-status={status}>
      <Icon className="size-3.5" aria-hidden />
      {t(status)}
    </Badge>
  );
}

export function useTypeLabel() {
  const t = useTranslations("promotions.type");
  return React.useCallback((type: string) => (t.has(type) ? t(type) : type), [t]);
}

export function useTypeOptions(): SelectOption[] {
  const t = useTranslations("promotions.type");
  return React.useMemo(() => PROMOTION_TYPES.map((x) => ({ value: x, label: t(x) })), [t]);
}

export function useStatusOptions(): SelectOption[] {
  const t = useTranslations("promotions.status");
  return React.useMemo(() => PROMOTION_STATUSES.map((x) => ({ value: x, label: t(x) })), [t]);
}
