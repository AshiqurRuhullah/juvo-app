"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, CircleX, FlaskConical, RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber } from "@/modules/catalog/format";
import { useIntegrationHealth, useSystemStatus, useTestIntegration, type IntegrationHealth, type IntegrationTestResult } from "./api";
import { ErrorText, Fact, StatusBadge } from "./common";
import { HEALTH_GROUPS, formatMs, hasNoCalls, healthGroup, shownStatus, type IntegrationStatus } from "./logic";
import { StatusPanel } from "./status-panel";

/**
 * `/admin/system/health`: one card per integration (payment gateways, couriers, SMS, e-mail, WhatsApp) and the system status panel.
 * Everything refreshes every 30 seconds (paused while the tab is hidden) and with the Refresh button. State is kept in the server's
 * memory and starts empty after a restart, so a slot nobody has called yet says "no calls recorded yet" instead of "healthy".
 */
export function HealthView() {
  const t = useTranslations("sysops.health");
  const dateTime = useDateTime();
  const queryClient = useQueryClient();
  const health = useIntegrationHealth();
  const status = useSystemStatus();
  const test = useTestIntegration();
  const problemText = useProblemMessage();
  const [tests, setTests] = React.useState<Record<string, IntegrationTestResult>>({});

  const rows = React.useMemo(() => health.data ?? [], [health.data]);
  const refreshing = health.isFetching || status.isFetching;
  const updated = Math.max(health.dataUpdatedAt, status.dataUpdatedAt);

  const runTest = async (key: string) => {
    try {
      const answer = await test.mutateAsync(key);
      setTests((all) => ({ ...all, [key]: answer }));
    } catch (failure) {
      // 409 SYSTEM.NOT_SUPPORTED and the others are explained in plain words, never as a raw error.
      toast.error(problemText(failure instanceof ApiError ? failure.problem : null));
    }
  };

  return (
    <div className="space-y-8" data-testid="health-view">
      <div>
        <div className="flex flex-wrap items-center gap-3">
          <p className="max-w-3xl flex-1 text-muted">{t("subtitle")}</p>
          <p className="text-sm text-muted" role="status" data-testid="health-updated">
            {updated ? t("updated", { when: dateTime(new Date(updated).toISOString()) }) : null}
          </p>
          <Button variant="secondary" loading={refreshing} onClick={() => void queryClient.invalidateQueries({ queryKey: ["system"] })} data-testid="health-refresh">
            <RefreshCw aria-hidden /> {t("refresh")}
          </Button>
        </div>
        <p className="mt-1 text-sm text-muted">{t("autoRefresh")}</p>
      </div>

      <section aria-labelledby="integrations-title" data-testid="integrations">
        <h2 id="integrations-title" className="font-display text-xl font-semibold text-strong">
          {t("integrations")}
        </h2>
        <p className="mt-1 max-w-3xl text-sm text-muted" data-testid="memory-note">
          {t("memoryNote")}
        </p>
        <div className="mt-4 space-y-6">
          {health.isPending ? (
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3" aria-busy="true">
              <Skeleton className="h-40" />
              <Skeleton className="h-40" />
              <Skeleton className="h-40" />
            </div>
          ) : health.isError ? (
            <Alert variant="danger">{t("loadFailed")}</Alert>
          ) : rows.length === 0 ? (
            <EmptyState title={t("none.title")} description={t("none.description")} />
          ) : (
            HEALTH_GROUPS.map((group) => {
              const inGroup = rows.filter((r) => healthGroup(r.kind) === group);
              if (inGroup.length === 0) return null;
              return (
                <div key={group}>
                  <h3 className="mb-2 text-lg font-semibold text-strong">{t(`groups.${group}`)}</h3>
                  <ul className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                    {inGroup.map((row) => (
                      <IntegrationCard key={row.key} row={row} result={tests[row.key]} testing={test.isPending && test.variables === row.key} onTest={() => void runTest(row.key)} />
                    ))}
                  </ul>
                </div>
              );
            })
          )}
        </div>
      </section>

      <StatusPanel query={status} />
    </div>
  );
}

function IntegrationCard({ row, result, testing, onTest }: { row: IntegrationHealth; result: IntegrationTestResult | undefined; testing: boolean; onTest: () => void }) {
  const t = useTranslations("sysops.health");
  const dateTime = useDateTime();
  const shown = shownStatus({ status: row.status as IntegrationStatus, lastSuccessAt: row.lastSuccessAt, lastFailureAt: row.lastFailureAt });
  const failures = toNumber(row.consecutiveFailures);
  return (
    <li className="flex flex-col rounded-card border border-line bg-card p-4 shadow-card" data-testid="integration-card" data-key={row.key} data-status={shown}>
      <div className="flex items-start justify-between gap-3">
        <h4 className="min-w-0 font-semibold text-strong [overflow-wrap:anywhere]" data-testid="integration-name">
          {row.name}
        </h4>
        <StatusBadge status={shown} />
      </div>
      <p className="mt-1 text-sm text-muted">
        {t("mode")}: <span data-testid="integration-mode">{t(`modes.${row.mode === "Live" ? "Live" : "Sandbox"}`)}</span>
      </p>

      {hasNoCalls(row) ? (
        <p className="mt-3 text-sm text-muted" data-testid="no-calls">
          {row.enabled ? t("noCalls") : t("disabledNote")}
        </p>
      ) : (
        <dl className="mt-3 grid gap-2 text-sm">
          <Fact label={t("lastSuccess")}>{row.lastSuccessAt ? dateTime(row.lastSuccessAt) : t("never")}</Fact>
          <Fact label={t("lastFailure")}>{row.lastFailureAt ? dateTime(row.lastFailureAt) : t("never")}</Fact>
          {row.lastError ? (
            <Fact label={t("lastError")} testId="integration-error">
              <ErrorText value={row.lastError} />
            </Fact>
          ) : null}
          <Fact label={t("failuresInRow")}>
            <span className="tabular-nums" data-testid="integration-failures">
              {failures}
            </span>
          </Fact>
        </dl>
      )}

      <div className="mt-auto pt-3">
        {row.canTest ? (
          <Button variant="secondary" loading={testing} onClick={onTest} data-testid="integration-test">
            <FlaskConical aria-hidden /> {t("test")}
          </Button>
        ) : (
          <p className="text-sm text-muted" data-testid="test-unavailable">
            {t("testUnavailable")}
          </p>
        )}
        {result ? (
          <p className="mt-2 flex items-start gap-2 text-sm text-strong" role="status" data-testid="integration-test-result" data-ok={result.ok}>
            {result.ok ? <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-success-ink" aria-hidden /> : <CircleX className="mt-0.5 size-4 shrink-0 text-danger-ink" aria-hidden />}
            <span className="[overflow-wrap:anywhere]">
              {result.ok ? t("testOk", { ms: formatMs(result.durationMs) }) : t("testFailed", { ms: formatMs(result.durationMs) })} {result.message}
            </span>
          </p>
        ) : null}
      </div>
    </li>
  );
}
