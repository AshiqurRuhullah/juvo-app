"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Lock, Pencil, Plus, Power, RefreshCw, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { toNumber } from "@/modules/catalog/format";
import { useApiKeys, useDeleteKey, useRotateKey, useSetKeyEnabled, type ApiKey } from "./api";
import { KeyStateBadge, ScopeChips, useIntegrationProblem, useIntegrationRights } from "./common";
import { HowToUse } from "./how-to-use";
import { KeyDialog } from "./key-dialog";
import { isoToDhakaDay, keyState } from "./logic";
import { SecretDialog, type OneTimeSecret } from "./secret-dialog";

type Filters = { enabled?: string };

/**
 * `/admin/integrations/api-keys`: every key with its prefix (never the secret), scopes, branch limit, state and last use. Creating or
 * rotating a key ends in the one-time secret dialog. Changes follow the staff member's rights; view-only staff get a read-only list.
 */
export function KeysList() {
  const t = useTranslations("integrations.keys");
  const tc = useTranslations("integrations.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const rights = useIntegrationRights();
  const problem = useIntegrationProblem();
  const state = useListState<Filters>({});
  const query = useApiKeys({ Page: state.page, PageSize: state.pageSize, ...(state.search ? { Search: state.search } : {}), ...(state.filters.enabled ? { enabled: state.filters.enabled } : {}) });
  const setEnabled = useSetKeyEnabled();
  const rotate = useRotateKey();
  const remove = useDeleteKey();
  const [editing, setEditing] = React.useState<ApiKey | "new" | null>(null);
  const [rotating, setRotating] = React.useState<ApiKey | null>(null);
  const [deleting, setDeleting] = React.useState<ApiKey | null>(null);
  const [secret, setSecret] = React.useState<OneTimeSecret | null>(null);

  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const filtersActive = !!(state.searchText || state.filters.enabled);
  const canChange = rights.keysManage;

  const toggle = async (key: ApiKey) => {
    const on = !key.isEnabled;
    try {
      await setEnabled.mutateAsync({ id: key.id, enabled: on });
      toast.success(on ? t("enabledToast", { name: key.name }) : t("disabledToast", { name: key.name }));
    } catch (e) {
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const doRotate = async () => {
    if (!rotating) return;
    const key = rotating;
    try {
      const answer = await rotate.mutateAsync(key.id);
      setRotating(null);
      setSecret({ kind: "key", value: answer.secret, name: answer.key.name, reason: "rotated" });
    } catch (e) {
      setRotating(null);
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const doDelete = async () => {
    if (!deleting) return;
    const key = deleting;
    try {
      await remove.mutateAsync(key.id);
      setDeleting(null);
      toast.success(t("deletedToast", { name: key.name }));
    } catch (e) {
      setDeleting(null);
      toast.error(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const columns: ColumnDef<ApiKey, unknown>[] = [
    {
      id: "name",
      header: t("columns.name"),
      enableSorting: false,
      meta: { primary: true },
      cell: ({ row }) => (
        <span className="block max-w-xs [overflow-wrap:anywhere]">
          <span className="font-semibold text-strong" data-testid="key-name">
            {row.original.name}
          </span>
          <span className="block font-mono text-sm text-muted" data-testid="key-prefix">
            {row.original.keyPrefix}…
          </span>
        </span>
      ),
    },
    { id: "scopes", header: t("columns.scopes"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <ScopeChips scopes={row.original.scopes} /> },
    {
      id: "branch",
      header: t("columns.branch"),
      enableSorting: false,
      meta: { secondary: true },
      cell: ({ row }) => <span data-testid="key-branch">{row.original.branchCode ?? t("allBranches")}</span>,
    },
    { id: "state", header: t("columns.state"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <KeyStateBadge state={keyState(row.original)} /> },
    {
      id: "expires",
      header: t("columns.expires"),
      enableSorting: false,
      meta: { secondary: true },
      cell: ({ row }) => <span className="text-sm">{row.original.expiresAt ? t("expiresOn", { day: isoToDhakaDay(row.original.expiresAt) }) : t("never")}</span>,
    },
    {
      id: "limit",
      header: t("columns.limit"),
      enableSorting: false,
      meta: { hideOnMobile: true },
      cell: ({ row }) => <span className="text-sm tabular-nums">{row.original.rateLimitPerMinute === null ? t("defaultLimit") : t("perMinute", { count: toNumber(row.original.rateLimitPerMinute) })}</span>,
    },
    {
      id: "lastUsed",
      header: t("columns.lastUsed"),
      enableSorting: false,
      meta: { secondary: true },
      cell: ({ row }) => <span className="text-sm text-muted" data-testid="key-last-used">{row.original.lastUsedAt ? dateTime(row.original.lastUsedAt) : t("neverUsed")}</span>,
    },
    {
      id: "created",
      header: t("columns.created"),
      enableSorting: false,
      meta: { hideOnMobile: true },
      cell: ({ row }) => <span className="text-sm text-muted">{dateTime(row.original.createdAt)}</span>,
    },
    {
      id: "actions",
      header: "",
      enableSorting: false,
      meta: { actions: true, align: "end", label: t("columns.actions") },
      cell: ({ row }) => {
        const k = row.original;
        if (!canChange) return null;
        return (
          <span className="flex flex-col items-stretch gap-2 md:flex-row md:flex-wrap md:justify-end">
            <Button variant="secondary" onClick={() => setEditing(k)} aria-label={t("editLabel", { name: k.name })} data-testid="key-edit">
              <Pencil aria-hidden /> {t("edit")}
            </Button>
            <Button variant="secondary" onClick={() => setRotating(k)} aria-label={t("rotateLabel", { name: k.name })} data-testid="key-rotate">
              <RefreshCw aria-hidden /> {t("rotate")}
            </Button>
            <Button variant="secondary" onClick={() => void toggle(k)} disabled={setEnabled.isPending} aria-label={k.isEnabled ? t("disableLabel", { name: k.name }) : t("enableLabel", { name: k.name })} data-testid="key-toggle">
              <Power aria-hidden /> {k.isEnabled ? t("disable") : t("enable")}
            </Button>
            <Button variant="secondary" onClick={() => setDeleting(k)} aria-label={t("deleteLabel", { name: k.name })} data-testid="key-delete">
              <Trash2 aria-hidden /> {t("delete")}
            </Button>
          </span>
        );
      },
    },
  ];

  return (
    <div className="space-y-4" data-testid="api-keys">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-muted">{t("subtitle")}</p>
        {canChange ? (
          <Button onClick={() => setEditing("new")} data-testid="key-new">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>

      {!canChange ? (
        <Alert variant="info" data-testid="keys-read-only">
          <span className="flex items-start gap-2">
            <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("readOnly")}</span>
          </span>
        </Alert>
      ) : null}

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect
          value={state.filters.enabled}
          onChange={(v) => state.setFilter("enabled", v)}
          options={[
            { value: "true", label: t("filterEnabled") },
            { value: "false", label: t("filterDisabled") },
          ]}
          allLabel={t("anyState")}
          label={t("columns.state")}
          searchPlaceholder={tc("search")}
          emptyText={tc("noMatch")}
        />
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={items}
        loading={query.isLoading}
        getRowId={(k) => k.id}
        labels={{ ...labels, empty: filtersActive ? labels.empty : t("empty"), emptyDescription: filtersActive ? labels.emptyDescription : t("emptyDescription") }}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
      />

      <HowToUse />

      <KeyDialog target={editing} onClose={() => setEditing(null)} onSecret={setSecret} />
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
      <SecretDialog secret={secret} onDone={() => setSecret(null)} returnFocusTo='[data-testid="key-new"]' />
    </div>
  );
}
