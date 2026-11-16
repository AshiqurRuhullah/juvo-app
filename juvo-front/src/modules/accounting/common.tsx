"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ArrowDownLeft, ArrowUpRight, CircleX, Minus } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { EXPENSE_TONE, addSubmitted, flowOf, wasSubmitted } from "./logic";

export { useDateOnly, useMoney2 } from "@/modules/purchasing/common";

/** Status of an expense. The words carry the meaning; a rejection also carries an icon so colour is never alone. */
export const ExpenseStatusBadge = React.memo(function ExpenseStatusBadge({ status, auto }: { status: string; auto?: boolean }) {
  const t = useTranslations("accounting.status");
  return (
    <span className="inline-flex flex-wrap items-center gap-1.5">
      <Badge variant={EXPENSE_TONE[status] ?? "neutral"} data-testid="expense-status">
        {status === "Rejected" ? <CircleX className="size-3.5" aria-hidden /> : null}
        {t.has(status) ? t(status) : status}
      </Badge>
      {auto ? <Badge variant="outline">{t("auto")}</Badge> : null}
    </span>
  );
});

/** A signed cash amount with a word and an arrow: money in, money out or nothing, so colour is never the only signal. */
export function Flow({ amount, format }: { amount: number; format: (value: number) => string }) {
  const t = useTranslations("accounting.flow");
  const dir = flowOf(amount);
  const Icon = dir === "in" ? ArrowDownLeft : dir === "out" ? ArrowUpRight : Minus;
  return (
    <span className={dir === "out" ? "inline-flex items-center gap-1 text-danger-ink" : "inline-flex items-center gap-1 text-strong"} data-flow={dir}>
      <Icon className="size-4 shrink-0" aria-hidden />
      <span className="tabular-nums">{format(Math.abs(amount))}</span>
      <span className="text-xs font-medium text-muted">{t(dir)}</span>
    </span>
  );
}

const KEY = (userId: string | null) => `juvo.expenses.submitted.${userId ?? "anon"}`;

/** Remembers on this device that this person sent an expense for approval (the API does not name the sender). */
export function rememberSubmitted(userId: string | null, id: string) {
  try {
    localStorage.setItem(KEY(userId), addSubmitted(localStorage.getItem(KEY(userId)), id));
  } catch {
    // Storage blocked: the approve button then stays visible and the API refuses self approval with its own text.
  }
}

export function isOwnSubmission(userId: string | null, id: string): boolean {
  try {
    return wasSubmitted(localStorage.getItem(KEY(userId)), id);
  } catch {
    return false;
  }
}
