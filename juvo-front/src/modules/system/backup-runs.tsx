"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { CheckCircle2, CircleX, Loader2, OctagonX, ShieldAlert } from "lucide-react";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { useDateTime, useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import type { BackupRun } from "./backup-api";
import { ErrorText } from "./common";
import { RUN_TONE, formatBytes, formatRunDuration, type RunStatus } from "./backup-logic";
import type { Tone } from "./logic";

const STATUS_ICON: Record<Tone, React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>> = { success: CheckCircle2, danger: OctagonX, info: Loader2, warning: ShieldAlert, neutral: CircleX };

export function RunStatusBadge({ status }: { status: RunStatus }) {
  const t = useTranslations("backups.runs.status");
  const tone = RUN_TONE[status];
  const Icon = STATUS_ICON[tone];
  return (
    <Badge variant={tone} data-testid="run-status" data-status={status}>
      <Icon className={status === "Running" ? "size-3.5 animate-spin" : "size-3.5"} aria-hidden />
      {t(status)}
    </Badge>
  );
}

/** Backup runs, newest first, ten per page. Phones get one card per run. */
export function BackupRuns({
  items,
  total,
  page,
  pageSize,
  onPageChange,
  loading,
  failed,
}: {
  items: BackupRun[];
  total: number;
  page: number;
  pageSize: number;
  onPageChange: (page: number) => void;
  loading: boolean;
  failed: boolean;
}) {
  const t = useTranslations("backups.runs");
  const tu = useTranslations("backups.units");
  const dateTime = useDateTime();
  const labels = useTableLabels();

  const columns = React.useMemo<ColumnDef<BackupRun, unknown>[]>(
    () => [
      {
        id: "started",
        header: t("columns.started"),
        meta: { primary: true },
        enableSorting: false,
        cell: ({ row }) => (
          <span className="font-semibold text-strong" data-testid="run-started">
            {dateTime(row.original.startedAt)}
          </span>
        ),
      },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, enableSorting: false, cell: ({ row }) => <RunStatusBadge status={row.original.status as RunStatus} /> },
      {
        id: "size",
        header: t("columns.size"),
        meta: { align: "end", label: t("columns.size") },
        enableSorting: false,
        cell: ({ row }) => (
          <span className="tabular-nums" data-testid="run-size">
            {row.original.sizeBytes === null || row.original.sizeBytes === undefined ? "-" : formatBytes(row.original.sizeBytes)}
          </span>
        ),
      },
      {
        id: "verified",
        header: t("columns.verified"),
        meta: { label: t("columns.verified") },
        enableSorting: false,
        cell: ({ row }) =>
          row.original.verified ? (
            <span className="inline-flex items-center gap-1 text-success-ink" data-testid="run-verified" data-verified="true">
              <CheckCircle2 className="size-4" aria-hidden /> {t("verifiedYes")}
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 text-muted" data-testid="run-verified" data-verified="false">
              <CircleX className="size-4" aria-hidden /> {t("verifiedNo")}
            </span>
          ),
      },
      {
        id: "trigger",
        header: t("columns.trigger"),
        meta: { label: t("columns.trigger") },
        enableSorting: false,
        cell: ({ row }) => <span data-testid="run-trigger">{t(`trigger.${row.original.trigger === "Schedule" ? "Schedule" : "User"}`)}</span>,
      },
      {
        id: "duration",
        header: t("columns.duration"),
        meta: { align: "end", label: t("columns.duration") },
        enableSorting: false,
        cell: ({ row }) => <span className="tabular-nums">{formatRunDuration(row.original.durationSeconds, { min: tu("min"), s: tu("s") })}</span>,
      },
      {
        id: "error",
        header: t("columns.error"),
        meta: { label: t("columns.error") },
        enableSorting: false,
        cell: ({ row }) => (
          <span data-testid="run-error">
            <ErrorText value={row.original.error} />
          </span>
        ),
      },
    ],
    [t, dateTime, tu],
  );

  return (
    <div className="space-y-4" data-testid="backup-runs">
      {failed ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("title")}
        columns={columns}
        data={items}
        loading={loading}
        getRowId={(x) => x.id}
        labels={{ ...labels, empty: t("empty"), emptyDescription: t("emptyDescription") }}
        page={page}
        pageSize={pageSize}
        total={toNumber(total)}
        onPageChange={onPageChange}
      />
    </div>
  );
}
