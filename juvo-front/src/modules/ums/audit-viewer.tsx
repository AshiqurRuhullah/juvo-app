"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { useAllBranches, useAuditLogs, type AuditLog } from "./api";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "./shared";

const ACTIONS = ["Created", "Updated", "Deleted", "AccessDenied"] as const;

/** Start and end of a calendar day as ISO text for the API. */
const dayStart = (date: string) => (date ? new Date(`${date}T00:00:00`).toISOString() : undefined);
const dayEnd = (date: string) => (date ? new Date(`${date}T23:59:59.999`).toISOString() : undefined);

/** Who changed what and when. Admin changes cannot be edited or deleted by staff (NFR-012). */
export function AuditViewer() {
  const t = useTranslations("ums.audit");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const branches = useAllBranches();
  const state = useListState<{ action?: string; branchId?: string; entity?: string; from?: string; to?: string }>({}, 25);
  const [selected, setSelected] = React.useState<AuditLog | null>(null);

  const query = useAuditLogs({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    action: state.filters.action as (typeof ACTIONS)[number] | undefined,
    branchId: state.filters.branchId,
    entity: state.filters.entity,
    from: dayStart(state.filters.from ?? ""),
    to: dayEnd(state.filters.to ?? ""),
  });

  const branchCode = React.useCallback((id: string | null) => (id ? (branches.data?.items.find((b) => b.id === id)?.code ?? "-") : "-"), [branches.data]);

  const columns = React.useMemo<ColumnDef<AuditLog, unknown>[]>(
    () => [
      { id: "occurredAt", header: t("columns.when"), meta: { primary: true }, cell: ({ row }) => dateTime(row.original.occurredAt, "long") },
      { id: "user", header: t("columns.who"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.userName ?? t("system")}</span> },
      {
        id: "action",
        header: t("columns.action"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <Badge variant={row.original.action === "Deleted" || row.original.action === "AccessDenied" ? "danger" : row.original.action === "Created" ? "success" : "info"}>{t(`actions.${row.original.action}`)}</Badge>,
      },
      {
        id: "entity",
        header: t("columns.entity"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block min-w-0">
            <span className="font-medium">{row.original.entity}</span>
            <span className="block truncate text-xs text-muted" title={row.original.entityId}>
              {row.original.entityId}
            </span>
          </span>
        ),
      },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => branchCode(row.original.branchId) },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.details") },
        cell: ({ row }) => (
          <Button variant="secondary" onClick={() => setSelected(row.original)} aria-label={t("openDetails", { entity: row.original.entity })}>
            {t("details")}
          </Button>
        ),
      },
    ],
    [t, dateTime, branchCode],
  );

  const filtersActive = !!(state.searchText || state.filters.action || state.filters.branchId || state.filters.entity || state.filters.from || state.filters.to);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 md:grid-cols-2 xl:grid-cols-6">
        <SearchBox className="xl:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect
          value={state.filters.action}
          onChange={(v) => state.setFilter("action", v)}
          options={ACTIONS.map((a) => ({ value: a, label: t(`actions.${a}`) }))}
          allLabel={t("filters.anyAction")}
          label={t("filters.action")}
          searchPlaceholder={t("filters.search")}
          emptyText={t("filters.noMatch")}
        />
        <FilterSelect
          value={state.filters.branchId}
          onChange={(v) => state.setFilter("branchId", v)}
          options={(branches.data?.items ?? []).map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` }))}
          allLabel={t("filters.anyBranch")}
          label={t("filters.branch")}
          searchPlaceholder={t("filters.search")}
          emptyText={t("filters.noMatch")}
        />
        <div>
          <label htmlFor="audit-from" className="sr-only">
            {t("filters.from")}
          </label>
          <Input id="audit-from" type="date" value={state.filters.from ?? ""} onChange={(e) => state.setFilter("from", e.target.value || undefined)} aria-label={t("filters.from")} />
        </div>
        <div>
          <label htmlFor="audit-to" className="sr-only">
            {t("filters.to")}
          </label>
          <Input id="audit-to" type="date" value={state.filters.to ?? ""} onChange={(e) => state.setFilter("to", e.target.value || undefined)} aria-label={t("filters.to")} />
        </div>
      </div>
      <div className="flex flex-wrap items-end gap-3">
        <Field label={t("filters.entity")} htmlFor="audit-entity" className="w-full sm:w-64">
          <Input id="audit-entity" value={state.filters.entity ?? ""} onChange={(e) => state.setFilter("entity", e.target.value || undefined)} placeholder={t("filters.entityPlaceholder")} />
        </Field>
        {filtersActive ? (
          <Button variant="ghost" onClick={state.resetFilters}>
            {t("filters.clear")}
          </Button>
        ) : null}
      </div>

      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(a) => a.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
      />

      <AuditDetails entry={selected} onClose={() => setSelected(null)} branchCode={branchCode} />
    </div>
  );
}

function parse(json: string | null | undefined): Record<string, unknown> | null {
  if (!json) return null;
  try {
    const value = JSON.parse(json);
    return value && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : { value };
  } catch {
    return { value: json };
  }
}

const show = (value: unknown) => (value === null || value === undefined ? "-" : typeof value === "object" ? JSON.stringify(value) : String(value));

function AuditDetails({ entry, onClose, branchCode }: { entry: AuditLog | null; onClose: () => void; branchCode: (id: string | null) => string }) {
  const t = useTranslations("ums.audit.detail");
  const tc = useTranslations("ums.common");
  const dateTime = useDateTime();

  const rows = React.useMemo(() => {
    if (!entry) return [];
    const before = parse(entry.oldValue);
    const after = parse(entry.newValue);
    const keys = [...new Set([...Object.keys(before ?? {}), ...Object.keys(after ?? {})])].sort();
    return keys.map((key) => ({ key, before: before?.[key], after: after?.[key], changed: show(before?.[key]) !== show(after?.[key]) }));
  }, [entry]);

  return (
    <Dialog open={entry !== null} onOpenChange={(open) => !open && onClose()}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        {entry ? (
          <div className="space-y-4">
            <div>
              <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title", { entity: entry.entity })}</DialogTitle>
              <DialogDescription className="mt-1 text-muted">{t("subtitle", { when: dateTime(entry.occurredAt, "long"), who: entry.userName ?? t("system") })}</DialogDescription>
            </div>
            <dl className="grid gap-3 text-sm sm:grid-cols-2">
              <Meta label={t("entityId")} value={entry.entityId} />
              <Meta label={t("branch")} value={branchCode(entry.branchId)} />
              <Meta label={t("ip")} value={entry.ipAddress ?? "-"} />
              <Meta label={t("correlation")} value={entry.correlationId ?? "-"} />
            </dl>
            {rows.length === 0 ? (
              <p className="text-muted">{t("noValues")}</p>
            ) : (
              <div className="scroll-thin overflow-x-auto rounded-card border border-line">
                <table className="w-full border-collapse text-start text-sm">
                  <caption className="sr-only">{t("changes")}</caption>
                  <thead className="bg-sunken text-muted">
                    <tr>
                      <th scope="col" className="px-3 py-2 text-start">{t("field")}</th>
                      <th scope="col" className="px-3 py-2 text-start">{t("before")}</th>
                      <th scope="col" className="px-3 py-2 text-start">{t("after")}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r) => (
                      <tr key={r.key} className={`border-t border-line align-top ${r.changed ? "bg-primary-soft/40" : ""}`}>
                        <th scope="row" className="px-3 py-2 text-start font-medium">
                          {r.key}
                          {r.changed ? <span className="sr-only"> ({t("changedFlag")})</span> : null}
                        </th>
                        <td className="px-3 py-2 [overflow-wrap:anywhere]">{show(r.before)}</td>
                        <td className="px-3 py-2 font-medium [overflow-wrap:anywhere]">{show(r.after)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            <div className="flex justify-end">
              <Button onClick={onClose}>{tc("close")}</Button>
            </div>
          </div>
        ) : null}
      </ModalContent>
    </Dialog>
  );
}

function Meta({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-muted">{label}</dt>
      <dd className="font-medium text-strong [overflow-wrap:anywhere]">{value}</dd>
    </div>
  );
}
