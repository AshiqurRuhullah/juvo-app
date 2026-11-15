"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Check, CircleAlert, CircleCheck, CircleX, TriangleAlert } from "lucide-react";
import { useSession } from "@/components/providers/session-provider";
import { Badge } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { settlementTone } from "@/modules/couriers/status";
import { num } from "./logic";

/** Money in taka with two decimals always (cash sheets are read digit by digit), in the current language. */
export function useMoney2() {
  const format = useFormatter();
  return React.useCallback(
    (value: number | string | null | undefined) =>
      value === null || value === undefined ? "-" : format.number(num(value), { style: "currency", currency: "BDT", currencyDisplay: "narrowSymbol", minimumFractionDigits: 2, maximumFractionDigits: 2 }),
    [format],
  );
}

/** Branch code and name by id, from the signed-in person's own branches. */
export function useBranchLabel() {
  const { me } = useSession();
  return React.useCallback(
    (id: string) => {
      const b = me.branches.find((x) => x.id === id);
      return b ? { code: b.code, name: b.nameEn, label: `${b.code} · ${b.nameEn}` } : { code: "-", name: "-", label: "-" };
    },
    [me.branches],
  );
}

/** Status of a batch. The words carry the meaning; finished states also carry an icon. */
export function SettlementStatusBadge({ status, className }: { status: string; className?: string }) {
  const t = useTranslations("cod.status");
  return (
    <Badge variant={settlementTone(status)} className={className} data-testid="settlement-status">
      {status === "Settled" ? <CircleCheck className="size-3.5" aria-hidden /> : null}
      {status === "Cancelled" ? <CircleX className="size-3.5" aria-hidden /> : null}
      {t.has(status) ? t(status) : status}
    </Badge>
  );
}

/** Short is danger with an icon and the word, over is a warning with an icon and the word, exact is plain. Nothing before reconciling. */
export function Difference({ difference, className }: { difference: number | string | null | undefined; className?: string }) {
  const t = useTranslations("cod.difference");
  const money = useMoney2();
  if (difference === null || difference === undefined) return <span className={cn("text-muted", className)}>-</span>;
  const d = num(difference);
  if (d < 0)
    return (
      <span className={cn("inline-flex items-center gap-1 font-semibold tabular-nums text-danger-ink", className)} data-variance="short">
        <CircleAlert className="size-4 shrink-0" aria-hidden /> {t("short", { amount: money(Math.abs(d)) })}
      </span>
    );
  if (d > 0)
    return (
      <span className={cn("inline-flex items-center gap-1 font-semibold tabular-nums text-warning-ink", className)} data-variance="over">
        <TriangleAlert className="size-4 shrink-0" aria-hidden /> {t("over", { amount: money(d) })}
      </span>
    );
  return (
    <span className={cn("inline-flex items-center gap-1 tabular-nums text-muted", className)} data-variance="exact">
      <Check className="size-4 shrink-0" aria-hidden /> {t("exact")}
    </span>
  );
}
