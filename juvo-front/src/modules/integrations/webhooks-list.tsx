"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { History, Lock, Pencil, Plus, Power, RefreshCw, Send, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { toNumber } from "@/modules/catalog/format";
import {
  useDeleteWebhook, useDeliveries, useRotateWebhookSecret, useSetWebhookEnabled, useTestWebhook, useWebhooks, type WebhookDelivery, type WebhookEndpoint,
} from "./api";
import { DeliveryStatusBadge, EventChips, useIntegrationProblem, useIntegrationRights } from "./common";
import { DeliveriesPanel } from "./deliveries-panel";
import { MAX_ENDPOINTS, type DeliveryStatus } from "./logic";
import { SecretDialog, type OneTimeSecret } from "./secret-dialog";
import { VerifyHelp } from "./verify-help";
import { WebhookDialog } from "./webhook-dialog";

/** The newest delivery of one endpoint (one small request per visible row; the API lists newest first). */
function LastDelivery({ endpointId }: { endpointId: string }) {
  const t = useTranslations("integrations.hooks");
  const dateTime = useDateTime();
  const query = useDeliveries({ Page: 1, PageSize: 1, endpointId });
  const last = query.data?.items[0];
  if (query.isLoading) return <span className="text-sm text-muted">…</span>;
  if (!last) {
    return (
      <span className="text-sm text-muted" data-testid="hook-no-delivery">
        {t("noDeliveries")}
      </span>
    );
  }
  return (
    <span className="flex flex-col items-start gap-1" data-testid="hook-last-delivery">
      <DeliveryStatusBadge status={last.status as DeliveryStatus} />
      <span className="text-sm text-muted">{dateTime(last.lastAttemptAt ?? last.createdAt)}</span>
    </span>
  );
}

/**
 * `/admin/integrations/webhooks`: the endpoints, their events, the newest delivery result, and the full delivery log below. A test
 * event is sent through the real sender, so an address that is not allowed answers with the refusal, which is shown here.
 */
export function WebhooksList() {
  const t = useTranslations("integrations.hooks");
  const tc = useTranslations("integrations.common");
  const labels = useTableLabels();
  const rights = useIntegrationRights();
  const problem = useIntegrationProblem();
  const state = useListState<Record<string, string | undefined>>({});
  const query = useWebhooks({ Page: state.page, PageSize: state.pageSize, ...(state.search ? { Search: state.search } : {}) });
  const setEnabled = useSetWebhookEnabled();
  const rotate = useRotateWebhookSecret();
  const remove = useDeleteWebhook();
  const test = useTestWebhook();
  const [editing, setEditing] = React.useState<WebhookEndpoint | "new" | null>(null);
  const [rotating, setRotating] = React.useState<WebhookEndpoint | null>(null);
  const [deleting, setDeleting] = React.useState<WebhookEndpoint | null>(null);
  const [secret, setSecret] = React.useState<OneTimeSecret | null>(null);
  const [testResult, setTestResult] = React.useState<{ name: string; delivery: WebhookDelivery } | null>(null);
  const [logEndpoint, setLogEndpoint] = React.useState<string | undefined>(undefined);

  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const total = toNumber(query.data?.total);
  const canChange = rights.hooksManage;
  const filtersActive = !!state.searchText;

  const toggle = async (hook: WebhookEndpoint) => {
    const on = !hook.isEnabled;
    try {
      await setEnabled.mutateAsync({ id: hook.id, enabled: on });
      toast.success(on ? t("enabledToast", { name: hook.name }) : t("disabledToast", { name: hook.name }));
    } catch (e) {
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const sendTest = async (hook: WebhookEndpoint) => {
    setTestResult(null);
    try {
      setTestResult({ name: hook.name, delivery: await test.mutateAsync(hook.id) });
    } catch (e) {
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const doRotate = async () => {
    if (!rotating) return;
    try {
      const answer = await rotate.mutateAsync(rotating.id);
      setRotating(null);
      setSecret({ kind: "webhook", value: answer.secret, name: answer.endpoint.name, reason: "rotated" });
    } catch (e) {
      setRotating(null);
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const doDelete = async () => {
    if (!deleting) return;
    const hook = deleting;
    try {
      await remove.mutateAsync(hook.id);
      setDeleting(null);
      if (logEndpoint === hook.id) setLogEndpoint(undefined);
      toast.success(t("deletedToast", { name: hook.name }));
    } catch (e) {
      setDeleting(null);
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const showLog = (hook: WebhookEndpoint) => {
    setLogEndpoint(hook.id);
    window.setTimeout(() => document.getElementById("delivery-log")?.scrollIntoView({ block: "start" }), 0);
  };

  const columns: ColumnDef<WebhookEndpoint, unknown>[] = [
    {
      id: "name",
      header: t("columns.name"),
      enableSorting: false,
      meta: { primary: true },
      cell: ({ row }) => (
        <span className="block max-w-xs [overflow-wrap:anywhere]">
          <span className="font-semibold text-strong" data-testid="hook-name">
            {row.original.name}
          </span>
          <span className="block font-mono text-sm text-muted" data-testid="hook-url">
            {row.original.url}
          </span>
        </span>
      ),
    },
    { id: "events", header: t("columns.events"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <EventChips events={row.original.events} /> },
    {
      id: "enabled",
      header: t("columns.enabled"),
      enableSorting: false,
      meta: { secondary: true },
      cell: ({ row }) => (
        <Badge variant={row.original.isEnabled ? "success" : "neutral"} data-testid="hook-state" data-enabled={row.original.isEnabled}>
          {row.original.isEnabled ? t("on") : t("off")}
        </Badge>
      ),
    },
    { id: "last", header: t("columns.lastDelivery"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <LastDelivery endpointId={row.original.id} /> },
    {
      id: "failed",
      header: t("columns.failed"),
      enableSorting: false,
      meta: { hideOnMobile: true, align: "end" },
      cell: ({ row }) => (
        <span className="tabular-nums" data-testid="hook-failed">
          {toNumber(row.original.failedDeliveries)}
        </span>
      ),
    },
    {
      id: "actions",
      header: "",
      enableSorting: false,
      meta: { actions: true, align: "end", label: t("columns.actions") },
      cell: ({ row }) => {
        const h = row.original;
        return (
          <span className="flex flex-col items-stretch gap-2 md:flex-row md:flex-wrap md:justify-end">
            <Button variant="secondary" onClick={() => showLog(h)} aria-label={t("logLabel", { name: h.name })} data-testid="hook-log">
              <History aria-hidden /> {t("log")}
            </Button>
            {canChange ? (
              <>
                <Button variant="secondary" onClick={() => void sendTest(h)} disabled={test.isPending} aria-label={t("testLabel", { name: h.name })} data-testid="hook-test">
                  <Send aria-hidden /> {t("test")}
                </Button>
                <Button variant="secondary" onClick={() => setEditing(h)} aria-label={t("editLabel", { name: h.name })} data-testid="hook-edit">
                  <Pencil aria-hidden /> {t("edit")}
                </Button>
                <Button variant="secondary" onClick={() => setRotating(h)} aria-label={t("rotateLabel", { name: h.name })} data-testid="hook-rotate">
                  <RefreshCw aria-hidden /> {t("rotate")}
                </Button>
                <Button variant="secondary" onClick={() => void toggle(h)} disabled={setEnabled.isPending} aria-label={h.isEnabled ? t("disableLabel", { name: h.name }) : t("enableLabel", { name: h.name })} data-testid="hook-toggle">
                  <Power aria-hidden /> {h.isEnabled ? t("disable") : t("enable")}
                </Button>
                <Button variant="secondary" onClick={() => setDeleting(h)} aria-label={t("deleteLabel", { name: h.name })} data-testid="hook-delete">
                  <Trash2 aria-hidden /> {t("delete")}
                </Button>
              </>
            ) : null}
          </span>
        );
      },
    },
  ];

  return (
    <div className="space-y-6" data-testid="webhooks">
      <section className="space-y-4" aria-labelledby="hooks-heading">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="max-w-2xl">
            <h2 id="hooks-heading" className="font-display text-xl font-semibold text-strong">
              {t("title")}
            </h2>
            <p className="mt-1 text-muted">{t("subtitle")}</p>
          </div>
          {canChange ? (
            <Button onClick={() => setEditing("new")} disabled={total >= MAX_ENDPOINTS} data-testid="hook-new">
              <Plus aria-hidden /> {t("new")}
            </Button>
          ) : null}
        </div>

        {!canChange ? (
          <Alert variant="info" data-testid="hooks-read-only">
            <span className="flex items-start gap-2">
              <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
              <span>{t("readOnly")}</span>
            </span>
          </Alert>
        ) : null}
        {canChange && total >= MAX_ENDPOINTS ? <Alert variant="warning">{t("limitReached", { max: MAX_ENDPOINTS })}</Alert> : null}

        <SearchBox className="lg:w-96" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />

        {testResult ? (
          <Alert variant={testResult.delivery.status === "Succeeded" ? "success" : "warning"} data-testid="test-result" data-status={testResult.delivery.status}>
            <span className="block space-y-1">
              <span className="block font-medium">{t("testTitle", { name: testResult.name })}</span>
              <span className="flex flex-wrap items-center gap-2">
                <DeliveryStatusBadge status={testResult.delivery.status as DeliveryStatus} />
                {testResult.delivery.httpStatus !== null && testResult.delivery.httpStatus !== undefined ? <span data-testid="test-http">{t("testHttp", { code: toNumber(testResult.delivery.httpStatus) })}</span> : null}
              </span>
              {testResult.delivery.error ? (
                <span className="block" data-testid="test-error">
                  {testResult.delivery.error}
                </span>
              ) : null}
              <Button variant="ghost" onClick={() => setTestResult(null)} className="-ms-3">
                {tc("dismiss")}
              </Button>
            </span>
          </Alert>
        ) : null}

        {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

        <DataTable
          caption={t("title")}
          columns={columns}
          data={items}
          loading={query.isLoading}
          getRowId={(h) => h.id}
          labels={{ ...labels, empty: filtersActive ? labels.empty : t("empty"), emptyDescription: filtersActive ? labels.emptyDescription : t("emptyDescription") }}
          page={state.page}
          pageSize={state.pageSize}
          total={total}
          onPageChange={state.setPage}
        />
      </section>

      <VerifyHelp />

      <DeliveriesPanel canRedeliver={canChange} endpointId={logEndpoint} onEndpointChange={setLogEndpoint} />

      <WebhookDialog target={editing} onClose={() => setEditing(null)} onSecret={setSecret} />
      <ConfirmDialog
        open={rotating !== null}
        onOpenChange={(open) => (open ? undefined : setRotating(null))}
        title={t("rotateTitle", { name: rotating?.name ?? "" })}
        description={t("rotateDescription")}
        confirmLabel={t("rotateConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={rotate.isPending}
        onConfirm={() => doRotate()}
      />
      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => (open ? undefined : setDeleting(null))}
        title={t("deleteTitle", { name: deleting?.name ?? "" })}
        description={t("deleteDescription")}
        confirmLabel={t("deleteConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={() => doDelete()}
      />
      <SecretDialog secret={secret} onDone={() => setSecret(null)} returnFocusTo='[data-testid="hook-new"]' />
    </div>
  );
}
