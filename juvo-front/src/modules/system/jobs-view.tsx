"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useQueryClient } from "@tanstack/react-query";
import { RefreshCw, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tabs } from "@/components/ui/tabs";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { useFailureKinds, useJobSummary, type JobSummary } from "./api";
import { ErrorText, useSystemPermissions } from "./common";
import { FailedPanel } from "./failed-panel";
import { OutboxPanel } from "./outbox-panel";
import { RunsPanel } from "./runs-panel";
import { toNumber } from "@/modules/catalog/format";

/** `/admin/system/jobs`: one card per job (last run, last success, failures in 24 h) above three tabs: job runs, queue, failed sends. */
export function JobsView() {
  const t = useTranslations("sysops.jobs");
  const perms = useSystemPermissions();
  const queryClient = useQueryClient();
  const summary = useJobSummary();
  const kinds = useFailureKinds();
  const [tab, setTab] = React.useState("runs");
  const tabs = [
    { id: "runs", label: t("tabs.runs") },
    { id: "outbox", label: t("tabs.outbox") },
    { id: "failed", label: t("tabs.failed") },
  ];
  const unrecorded = (kinds.data ?? []).filter((k) => !k.recorded).map((k) => k.kind);

  return (
    <div className="space-y-6" data-testid="jobs-view">
      <div className="flex flex-wrap items-center gap-3">
        <p className="max-w-3xl flex-1 text-muted">{t("subtitle")}</p>
        <Button variant="secondary" onClick={() => void queryClient.invalidateQueries({ queryKey: ["system"] })} data-testid="jobs-refresh">
          <RefreshCw aria-hidden /> {t("refresh")}
        </Button>
      </div>

      <section aria-labelledby="jobs-summary-title">
        <h2 id="jobs-summary-title" className="font-display text-xl font-semibold text-strong">
          {t("summaryTitle")}
        </h2>
        <div className="mt-3">
          {summary.isPending ? (
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" aria-busy="true">
              <Skeleton className="h-36" />
              <Skeleton className="h-36" />
              <Skeleton className="h-36" />
            </div>
          ) : summary.isError ? (
            <Alert variant="danger">{t("loadFailed")}</Alert>
          ) : summary.data.length === 0 ? (
            <EmptyState title={t("noJobs.title")} description={t("noJobs.description")} />
          ) : (
            <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" data-testid="job-cards">
              {summary.data.map((job) => (
                <JobCard key={job.jobName} job={job} />
              ))}
            </ul>
          )}
        </div>
      </section>

      {unrecorded.length > 0 ? (
        <Alert variant="info" data-testid="unrecorded-note">
          {t("unrecorded", { kinds: unrecorded.map((k) => (t.has(`kinds.${k}`) ? t(`kinds.${k}`) : k)).join(", ") })}
        </Alert>
      ) : null}

      <Tabs tabs={tabs} value={tab} onValueChange={setTab} label={t("title")}>
        {(active) => (active === "runs" ? <RunsPanel /> : active === "outbox" ? <OutboxPanel canRetry={perms.jobsRetry} /> : <FailedPanel canRetry={perms.jobsRetry} />)}
      </Tabs>
    </div>
  );
}

function JobCard({ job }: { job: JobSummary }) {
  const t = useTranslations("sysops.jobs");
  const dateTime = useDateTime();
  const failures = toNumber(job.failures24h);
  return (
    <li className="rounded-card border border-line bg-card p-4 shadow-card" data-testid="job-card" data-job={job.jobName}>
      <h3 className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]">{job.jobName}</h3>
      <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
        <dt className="text-muted">{t("lastRun")}</dt>
        <dd className="text-end text-strong">{job.lastRunAt ? dateTime(job.lastRunAt) : t("never")}</dd>
        <dt className="text-muted">{t("lastSuccess")}</dt>
        <dd className="text-end text-strong">{job.lastSuccessAt ? dateTime(job.lastSuccessAt) : t("never")}</dd>
        <dt className="text-muted">{t("runs24h")}</dt>
        <dd className="text-end tabular-nums text-strong">{toNumber(job.runs24h)}</dd>
        <dt className="text-muted">{t("failures24h")}</dt>
        <dd className={failures > 0 ? "text-end font-semibold tabular-nums text-danger-ink" : "text-end tabular-nums text-strong"} data-testid="job-failures">
          {failures > 0 ? (
            <span className="inline-flex items-center gap-1">
              <TriangleAlert className="size-4" aria-hidden />
              {t("failuresCount", { count: failures })}
            </span>
          ) : (
            failures
          )}
        </dd>
      </dl>
      {job.lastError ? (
        <p className="mt-3 text-sm">
          <span className="text-muted">{t("lastError")}: </span>
          <ErrorText value={job.lastError} />
        </p>
      ) : null}
    </li>
  );
}
