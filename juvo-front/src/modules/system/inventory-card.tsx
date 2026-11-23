"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { useDataInventory } from "./backup-api";
import { formatMegabytes, isKnownInventoryKey, orderInventory } from "./backup-logic";

/**
 * "What the system holds" (`GET /system/data-inventory`, permission system.health.view): counts only, no personal data. Lives on the
 * system health page because it needs exactly that permission (the Manager has it, the backup permission is Super Admin only).
 */
export function InventoryCard() {
  const t = useTranslations("backups.inventory");
  const dateTime = useDateTime();
  const query = useDataInventory();
  const rows = React.useMemo(() => orderInventory(query.data?.tables ?? []), [query.data]);

  return (
    <section aria-labelledby="inventory-title" data-testid="data-inventory">
      <h2 id="inventory-title" className="font-display text-xl font-semibold text-strong">
        {t("title")}
      </h2>
      <p className="mt-1 max-w-3xl text-sm text-muted" data-testid="inventory-note">
        {t("subtitle")}
      </p>
      <div className="mt-4">
        {query.isPending ? (
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" aria-busy="true">
            <Skeleton className="h-16" />
            <Skeleton className="h-16" />
            <Skeleton className="h-16" />
          </div>
        ) : query.isError ? (
          <Alert variant="danger">{t("loadFailed")}</Alert>
        ) : (
          <div className="rounded-card border border-line bg-card p-4 shadow-card">
            <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {rows.map((row) => (
                <div key={row.key} className="min-w-0" data-testid="inventory-row" data-key={row.key}>
                  <dt className="text-sm text-muted">{isKnownInventoryKey(row.key) ? t(`rows.${row.key}`) : row.key}</dt>
                  <dd className="text-lg font-semibold tabular-nums text-strong" data-testid="inventory-count">
                    {toNumber(row.rows)}
                  </dd>
                </div>
              ))}
              <div className="min-w-0" data-testid="inventory-uploads">
                <dt className="text-sm text-muted">{t("uploads")}</dt>
                <dd className="text-lg font-semibold tabular-nums text-strong">
                  {t("uploadsValue", { size: formatMegabytes(query.data.uploadsBytes), files: toNumber(query.data.uploadsFiles) })}
                </dd>
                {query.data.uploadsTruncated ? <dd className="text-sm text-muted">{t("uploadsTruncated", { files: toNumber(query.data.uploadsFiles) })}</dd> : null}
              </div>
            </dl>
            <p className="mt-3 text-sm text-muted" data-testid="inventory-at">
              {t("snapshot", { time: dateTime(query.data.at, "long") })}
            </p>
          </div>
        )}
      </div>
    </section>
  );
}
