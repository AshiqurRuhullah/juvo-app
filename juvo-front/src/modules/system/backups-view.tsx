"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useQueryClient } from "@tanstack/react-query";
import { DatabaseBackup, Loader2, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { canAnywhere } from "@/lib/auth/permissions";
import { PAGE_SIZE } from "@/lib/pagination";
import { toNumber } from "@/modules/catalog/format";
import { useBackupRuns, useBackupStatus, useStartBackup } from "./backup-api";
import { BackupRuns } from "./backup-runs";
import { BackupNotes, BackupStatusPanel } from "./backup-status";
import { anyRunning, startFailure } from "./backup-logic";

type Notice = { kind: "running" } | { kind: "error"; text: string } | { kind: "started" } | null;

/**
 * `/admin/system/backups` (FR-AF03): status of the newest verified backup, an honest notes block, "Back up now" (only with
 * system.backup.run), and the runs. There is no restore and no download here: restore is an operator script.
 */
export function BackupsView() {
  const t = useTranslations("backups");
  const tr = useTranslations("backups.run");
  const dateTime = useDateTime();
  const problemMessage = useProblemMessage();
  const queryClient = useQueryClient();
  const { me } = useSession();
  const canRun = canAnywhere(me, "system.backup.run");
  const canOpenSettings = canAnywhere(me, "settings.system.view");

  const [page, setPage] = React.useState(1);
  const status = useBackupStatus();
  const runs = useBackupRuns({ Page: page, PageSize: PAGE_SIZE }, !!status.data?.running);
  const start = useStartBackup();
  const [confirming, setConfirming] = React.useState(false);
  const [notice, setNotice] = React.useState<Notice>(null);

  const items = React.useMemo(() => runs.data?.items ?? [], [runs.data]);
  const running = start.isPending || anyRunning(status.data, items);
  const runningRun = status.data?.lastRun?.status === "Running" ? status.data.lastRun : items.find((r) => r.status === "Running");

  async function begin() {
    setNotice(null);
    try {
      await start.mutateAsync();
      setNotice({ kind: "started" });
    } catch (error) {
      const problem = error instanceof ApiError ? error.problem : undefined;
      const failure = startFailure(problem);
      // 409 means somebody else (or the night) already started one: calm news, the page then shows its progress.
      setNotice(failure === "running" ? { kind: "running" } : { kind: "error", text: problemMessage(problem) });
    } finally {
      setConfirming(false);
    }
  }

  return (
    <div className="space-y-6" data-testid="backups-view">
      <div className="flex flex-wrap items-center gap-3">
        <p className="max-w-3xl flex-1 text-muted">{t("subtitle")}</p>
        <Button variant="secondary" onClick={() => void queryClient.invalidateQueries({ queryKey: ["system", "backups"] })} data-testid="backups-refresh">
          <RefreshCw aria-hidden /> {t("refresh")}
        </Button>
        {canRun ? (
          <Button onClick={() => setConfirming(true)} disabled={running} data-testid="backup-now">
            <DatabaseBackup aria-hidden /> {tr("button")}
          </Button>
        ) : null}
      </div>

      {running ? (
        <Alert variant="info" title={start.isPending ? tr("starting") : tr("running")} data-testid="backup-progress">
          <p className="flex items-center gap-2">
            <Loader2 className="size-4 animate-spin" aria-hidden />
            {runningRun ? tr("runningText", { time: dateTime(runningRun.startedAt, "long") }) : tr("starting")}
          </p>
        </Alert>
      ) : null}
      {notice?.kind === "running" ? (
        <Alert variant="warning" data-testid="backup-already">
          {tr("already")}
        </Alert>
      ) : null}
      {notice?.kind === "error" ? (
        <Alert variant="danger" data-testid="backup-start-error">
          {notice.text}
        </Alert>
      ) : null}
      {notice?.kind === "started" && !running ? (
        <Alert variant="success" data-testid="backup-started">
          {tr("started")}
        </Alert>
      ) : null}

      <BackupStatusPanel status={status.data} loading={status.isPending} failed={status.isError} canOpenSettings={canOpenSettings} />
      <BackupNotes />

      <section aria-labelledby="backup-runs-title">
        <h2 id="backup-runs-title" className="mb-3 font-display text-xl font-semibold text-strong">
          {t("runs.title")}
        </h2>
        <BackupRuns items={items} total={toNumber(runs.data?.total)} page={page} pageSize={PAGE_SIZE} onPageChange={setPage} loading={runs.isLoading} failed={runs.isError} />
      </section>

      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={tr("confirmTitle")}
        description={tr("confirmText")}
        confirmLabel={tr("confirm")}
        cancelLabel={tr("cancel")}
        closeLabel={tr("close")}
        loading={start.isPending}
        onConfirm={begin}
      />
    </div>
  );
}
