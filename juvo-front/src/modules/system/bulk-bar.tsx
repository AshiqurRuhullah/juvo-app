"use client";

import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";

/**
 * The bar above a list with ticked rows: how many are ticked (never more than the API's limit), tick the whole page, clear, and the
 * one action (requeue or retry). Without the retry permission the screen does not draw it at all.
 */
export function BulkBar({
  count,
  limit,
  refused,
  actionLabel,
  onAction,
  onSelectPage,
  onClear,
  pageSelectable,
  busy,
}: {
  count: number;
  limit: number;
  refused: boolean;
  actionLabel: string;
  onAction: () => void;
  onSelectPage: () => void;
  onClear: () => void;
  pageSelectable: number;
  busy: boolean;
}) {
  const t = useTranslations("sysops.bulk");
  return (
    <div className="flex flex-col gap-3 rounded-card border border-line bg-card p-3 sm:flex-row sm:flex-wrap sm:items-center" data-testid="bulk-bar">
      <p className="font-semibold text-strong" role="status" data-testid="bulk-count">
        {t("selected", { count, limit })}
      </p>
      {refused ? (
        <p className="text-sm text-warning-ink" role="status" data-testid="bulk-limit">
          {t("limit", { limit })}
        </p>
      ) : null}
      <div className="flex flex-wrap gap-2 sm:ms-auto">
        <Button variant="secondary" onClick={onSelectPage} disabled={pageSelectable === 0} data-testid="bulk-select-page">
          {t("selectPage")}
        </Button>
        <Button variant="ghost" onClick={onClear} disabled={count === 0} data-testid="bulk-clear">
          {t("clear")}
        </Button>
        <Button onClick={onAction} disabled={count === 0} loading={busy} data-testid="bulk-action">
          {actionLabel}
        </Button>
      </div>
    </div>
  );
}
