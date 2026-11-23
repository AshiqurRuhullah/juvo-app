"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleCheck, CircleX, Lock, UserRound, UserRoundCheck, UserRoundPlus } from "lucide-react";
import { Alert, Badge } from "@/components/ui/surfaces";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import { levelTone, statusTone, type CustomerRights, type ProfileLevel } from "./logic";
import type { PrivacyRights } from "./privacy-logic";

/** What this staff member may do with customers (all global permissions). Display only: the API still refuses. */
export function useCustomerRights(): CustomerRights {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      view: canAnywhere(me, "customers.customer.view"),
      block: canAnywhere(me, "customers.customer.block"),
      viewPii: canAnywhere(me, "customers.customer.view-pii"),
      viewOrderStats: canAnywhere(me, "fraud.flag.view"),
    }),
    [me],
  );
}

/** The two privacy permissions (export and anonymise). Display only: the API still refuses. */
export function usePrivacyRights(): PrivacyRights {
  const { me } = useSession();
  return React.useMemo(() => ({ exportData: canAnywhere(me, "customers.privacy.export"), anonymise: canAnywhere(me, "customers.privacy.anonymise") }), [me]);
}

/** Active or deactivated, always with an icon and a word. */
export function ActiveBadge({ isActive }: { isActive: boolean }) {
  const t = useTranslations("customers.status");
  const Icon = isActive ? CircleCheck : CircleX;
  return (
    <Badge variant={statusTone(isActive)} data-testid="customer-status">
      <Icon className="size-3.5" aria-hidden />
      {isActive ? t("active") : t("inactive")}
    </Badge>
  );
}

const LEVEL_ICON: Record<ProfileLevel, React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>> = { Guest: UserRound, Quick: UserRoundPlus, Full: UserRoundCheck };

/** Profile level (guest, quick, full) as a badge. */
export function LevelBadge({ level }: { level: ProfileLevel }) {
  const t = useTranslations("customers.level");
  const Icon = LEVEL_ICON[level];
  return (
    <Badge variant={levelTone(level)} data-testid="customer-level">
      <Icon className="size-3.5" aria-hidden />
      {t(level)}
    </Badge>
  );
}

/** Said once, plainly, when this person lacks `customers.customer.view-pii`: the shown values are what the API sends, never unmasked here. */
export function MaskedNotice({ where }: { where: "list" | "detail" }) {
  const t = useTranslations("customers.masked");
  return (
    <Alert variant="info" data-testid="pii-notice">
      <span className="flex items-start gap-2">
        <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
        <span>
          <span className="block font-medium">{t("title")}</span>
          <span className="block">{where === "list" ? t("list") : t("detail")}</span>
        </span>
      </span>
    </Alert>
  );
}
