"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CheckCircle2, CircleHelp, CircleOff, FlaskConical, Loader2, OctagonX, TriangleAlert, type LucideIcon } from "lucide-react";
import { Alert, Badge } from "@/components/ui/surfaces";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import { cn } from "@/lib/utils";
import { OUTCOME_TONE, STATE_TONE, statusLook, summariseResult, type JobOutcome, type OutboxState, type RequeueResult, type ShownStatus, type StatusIcon, type Tone } from "./logic";

/** What this staff member may do on the system screens (display only; the API refuses the rest). The screens are platform-wide, not branch-scoped. */
export function useSystemPermissions() {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      jobsView: canAnywhere(me, "system.jobs.view"),
      jobsRetry: canAnywhere(me, "system.jobs.retry"),
      healthView: canAnywhere(me, "system.health.view"),
    }),
    [me],
  );
}

const ICONS: Record<StatusIcon, LucideIcon> = { check: CheckCircle2, alert: TriangleAlert, down: OctagonX, off: CircleOff, flask: FlaskConical, unknown: CircleHelp };

/** Integration status: colour, icon and text together (never colour alone). */
export function StatusBadge({ status }: { status: ShownStatus }) {
  const t = useTranslations("sysops.status");
  const look = statusLook(status);
  const Icon = ICONS[look.icon];
  return (
    <Badge variant={look.tone} data-testid="status-badge" data-status={status}>
      <Icon className="size-3.5" aria-hidden />
      {t(status)}
    </Badge>
  );
}

const TONE_ICON: Record<Tone, LucideIcon> = { success: CheckCircle2, warning: TriangleAlert, danger: OctagonX, neutral: CircleHelp, info: Loader2 };

export function RunOutcomeBadge({ outcome }: { outcome: JobOutcome }) {
  const t = useTranslations("sysops.outcome");
  const tone = OUTCOME_TONE[outcome];
  const Icon = TONE_ICON[tone];
  return (
    <Badge variant={tone} data-testid="outcome-badge" data-outcome={outcome}>
      <Icon className="size-3.5" aria-hidden />
      {t(outcome)}
    </Badge>
  );
}

export function QueueStateBadge({ state }: { state: OutboxState }) {
  const t = useTranslations("sysops.state");
  const tone = STATE_TONE[state];
  const Icon = TONE_ICON[tone];
  return (
    <Badge variant={tone} data-testid="state-badge" data-state={state}>
      <Icon className="size-3.5" aria-hidden />
      {t(state)}
    </Badge>
  );
}

/** One label and value pair for detail dialogs and cards. */
export function Fact({ label, children, className, testId }: { label: string; children: React.ReactNode; className?: string; testId?: string }) {
  return (
    <div className={cn("min-w-0", className)}>
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="text-strong [overflow-wrap:anywhere]" data-testid={testId}>
        {children}
      </dd>
    </div>
  );
}

/** The scrubbed error text of the API, or a dash. The API already hid secrets and cut it; the screen shows it as it came. */
export function ErrorText({ value, className }: { value: string | null | undefined; className?: string }) {
  return value ? <span className={cn("font-mono text-sm [overflow-wrap:anywhere]", className)}>{value}</span> : <span className="text-muted">-</span>;
}

/**
 * Result of a requeue or retry call: how many were done and, per item, why one was skipped. Shown in full after a bulk call so
 * nothing is silent.
 */
export function ResultNotice({ result, onDismiss, testId }: { result: RequeueResult; onDismiss: () => void; testId: string }) {
  const t = useTranslations("sysops.result");
  const summary = summariseResult(result);
  const variant = summary.outcome === "all" ? "success" : summary.outcome === "none" ? "warning" : "info";
  return (
    <Alert variant={variant} title={t("title", { done: summary.requeued, total: summary.requested })} data-testid={testId}>
      <p data-testid="result-counts">{t("counts", { done: summary.requeued, skipped: summary.skipped.length })}</p>
      {summary.skipped.length > 0 ? (
        <ul className="mt-2 space-y-1" data-testid="result-skipped">
          {summary.skipped.map((s) => (
            <li key={s.id} data-testid="result-skipped-row">
              <span className="font-mono text-xs">{s.id}</span>: {s.reasonKey ? t(`reason.${s.reasonKey}`) : s.reason}
            </li>
          ))}
        </ul>
      ) : null}
      <button type="button" onClick={onDismiss} className="mt-2 inline-flex min-h-11 items-center font-semibold underline underline-offset-2">
        {t("dismiss")}
      </button>
    </Alert>
  );
}
