"use client";

import * as React from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { CheckCircle2, Clock, OctagonX, TriangleAlert, type LucideIcon } from "lucide-react";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import type { BackupStatus } from "./backup-api";
import { Fact } from "./common";
import { ageHours, backupState, backupStateLook, formatAge, formatBytes, type BackupState, type BackupStateIcon } from "./backup-logic";

const ICONS: Record<BackupStateIcon, LucideIcon> = { check: CheckCircle2, alert: TriangleAlert, none: OctagonX, clock: Clock };

/** State of the newest verified backup: colour, icon and text together (never colour alone). */
export function BackupStateBadge({ state }: { state: BackupState }) {
  const t = useTranslations("backups.status.state");
  const look = backupStateLook(state);
  const Icon = ICONS[look.icon];
  return (
    <Badge variant={look.tone} data-testid="backup-state" data-state={state}>
      <Icon className="size-3.5" aria-hidden />
      {t(state)}
    </Badge>
  );
}

/** The panel at the top of the page: state, last verified backup and its age, folder, free space, retention and the next scheduled time. */
export function BackupStatusPanel({ status, loading, failed, canOpenSettings }: { status: BackupStatus | undefined; loading: boolean; failed: boolean; canOpenSettings: boolean }) {
  const t = useTranslations("backups");
  const ts = useTranslations("backups.status");
  const tu = useTranslations("backups.units");
  const dateTime = useDateTime();
  const format = useFormatter();
  // The API sends the age itself; `now` is only the fallback when it does not.
  const [now] = React.useState(() => new Date());

  if (loading) {
    return (
      <div className="grid gap-3 sm:grid-cols-2" aria-busy="true">
        <Skeleton className="h-28" />
        <Skeleton className="h-28" />
      </div>
    );
  }
  if (failed || !status) return <Alert variant="danger">{t("loadFailed")}</Alert>;

  const state = backupState(status, now);
  const age = ageHours(status, now);
  const units = { d: tu("d"), h: tu("h"), min: tu("min"), s: tu("s") };
  const free = status.folderFreeBytes === null || status.folderFreeBytes === undefined ? null : formatBytes(status.folderFreeBytes);
  // `dailyAt` is HH:mm in the API's time zone (Asia/Dhaka); the next time is shown in Dhaka time whatever the browser's zone.
  const next = status.nextScheduledAt
    ? format.dateTime(new Date(status.nextScheduledAt), { dateStyle: "medium", timeStyle: "short", timeZone: "Asia/Dhaka" })
    : null;

  return (
    <div className="space-y-4" data-testid="backup-status">
      {!status.enabled ? (
        <Alert variant="warning" title={t("off.title")} data-testid="backup-off">
          <p>{t("off.text")}</p>
          {canOpenSettings ? (
            <Link href="/admin/settings" className="mt-2 inline-flex min-h-11 items-center font-semibold underline underline-offset-2" data-testid="backup-settings-link">
              {t("off.link")}
            </Link>
          ) : null}
        </Alert>
      ) : null}

      <div className="rounded-card border border-line bg-card p-4 shadow-card">
        <div className="flex flex-wrap items-center gap-3">
          <BackupStateBadge state={state} />
          <p className="min-w-0 flex-1 text-strong" data-testid="backup-state-text">
            {ts(`stateText.${state}`)}
          </p>
        </div>
        <dl className="mt-4 grid gap-4 sm:grid-cols-2">
          <Fact label={ts("lastVerified")} testId="backup-last-verified">
            {status.lastVerifiedAt ? (
              <>
                {dateTime(status.lastVerifiedAt, "long")}
                {age !== null ? <span className="text-muted" data-testid="backup-age"> ({ts("age", { age: formatAge(age, units) })})</span> : null}
              </>
            ) : (
              ts("noneYet")
            )}
          </Fact>
          <Fact label={ts("file")} testId="backup-file">
            {status.lastVerifiedFileName ? <span className="font-mono text-sm">{status.lastVerifiedFileName}</span> : <span className="text-muted">-</span>}
          </Fact>
          <Fact label={ts("folder")} testId="backup-folder">
            <span className="font-mono text-sm">{status.folder}</span>
          </Fact>
          <Fact label={ts("freeSpace")} testId="backup-free">
            {free ?? <span className="text-muted">{ts("freeUnknown")}</span>}
          </Fact>
          <Fact label={ts("retention")} testId="backup-retention">
            {ts("retentionDays", { days: toNumber(status.retentionDays) })}
          </Fact>
          <Fact label={ts("next")} testId="backup-next">
            {next ? ts("nextDhaka", { time: next }) : <span className="text-muted">{ts("nextNone")}</span>}
            <span className="block text-sm text-muted">{ts("daily", { time: status.dailyAt, zone: status.timeZone })}</span>
          </Fact>
        </dl>
      </div>
    </div>
  );
}

/** What backups do not do, in plain words. Always shown. */
export function BackupNotes() {
  const t = useTranslations("backups.notes");
  return (
    <Alert variant="info" title={t("title")} data-testid="backup-notes">
      <ul className="list-disc space-y-1 ps-5">
        <li data-testid="note-disk">{t("disk")}</li>
        <li data-testid="note-encryption">{t("encryption")}</li>
        <li data-testid="note-uploads">{t("uploads")}</li>
        <li data-testid="note-restore">{t("restore")}</li>
      </ul>
      <p className="mt-2">{t("docs")}</p>
    </Alert>
  );
}
