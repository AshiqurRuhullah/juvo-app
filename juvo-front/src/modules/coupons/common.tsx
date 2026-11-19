"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CalendarClock, CircleCheck, CircleSlash, CircleX, Hourglass } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import type { SelectOption } from "@/components/ui/select";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import type { ApiProblem } from "@/lib/api/types";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { COUPON_STATUSES, type CouponRights, type CouponStatus } from "./logic";

/** What this staff member may do with coupons (they are global, not per branch). Display only: the API still refuses. */
export function useCouponRights(): CouponRights {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      view: canAnywhere(me, "promotions.coupon.view"),
      create: canAnywhere(me, "promotions.coupon.create"),
      edit: canAnywhere(me, "promotions.coupon.edit"),
      remove: canAnywhere(me, "promotions.coupon.delete"),
    }),
    [me],
  );
}

const ICON: Record<CouponStatus, React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>> = {
  Active: CircleCheck,
  Off: CircleX,
  NotStarted: CalendarClock,
  Expired: Hourglass,
  UsedUp: CircleSlash,
};

const TONE: Record<CouponStatus, "success" | "outline" | "info" | "warning" | "danger"> = {
  Active: "success",
  Off: "outline",
  NotStarted: "info",
  Expired: "warning",
  UsedUp: "warning",
};

/** Status as a chip with a word and an icon, so colour is never the only signal. */
export function StatusBadge({ status }: { status: CouponStatus }) {
  const t = useTranslations("coupons.status");
  const Icon = ICON[status];
  return (
    <Badge variant={TONE[status]} data-testid="coupon-status" data-status={status}>
      <Icon className="size-3.5" aria-hidden />
      {t(status)}
    </Badge>
  );
}

export function useStatusOptions(): SelectOption[] {
  const t = useTranslations("coupons.status");
  return React.useMemo(() => COUPON_STATUSES.map((x) => ({ value: x, label: t(x) })), [t]);
}

/**
 * Staff wording for a refused request: the coupon codes of this module first (they say what to do next), then the shared texts.
 * Field messages the API wrote for staff (validation) follow, so nothing is hidden.
 */
export function useCouponProblem() {
  const t = useTranslations("coupons.errors");
  const fallback = useProblemMessage();
  return (problem: ApiProblem | null | undefined): string => {
    const key = problem?.code?.replaceAll(".", "_");
    if (key && t.has(key)) return t(key);
    const fields = Object.values(problem?.errors ?? {}).flat();
    return fields.length ? fields.join(" ") : fallback(problem);
  };
}
