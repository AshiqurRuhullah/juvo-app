"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { CheckCircle2, CircleX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, Field, Input, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can, usePermissions } from "@/components/providers/session-provider";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { useBulkReturns, useReturns, useReturnSummary, type BulkResult, type ReturnListItem } from "./api";
import { ReturnStatusBadge, useReasonLabel } from "./common";
import { RETURN_FAULTS, RETURN_KINDS, RETURN_STATUSES, SUMMARY_CHIPS, type ReturnFault, type ReturnKind, type ReturnStatus } from "./logic";

type Filters = { status?: string; kind?: string; fault?: string; branchId?: string; from?: string; to?: string };

/** The returns queue: counts on top, filters, the table (cards on phones) and the bulk approve / reject bar. */
export function ReturnsQueue() {
  const t = useTranslations("returns.queue");
  const ts = useTranslations("returns.status");
  const tk = useTranslations("returns.kind");
  const tf = useTranslations("returns.fault");
  const tc = useTranslations("returns.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const money = useMoney();
  const reasonLabel = useReasonLabel();
  const branches = useBranchOptions();
  const { can } = usePermissions();
  const state = useListState<Filters>({}, 20);
  const [selected, setSelected] = React.useState<ReadonlySet<string>>(new Set());

  const query = useReturns({
    page: state.page,
    pageSize: state.pageSize,
    search: state.search,
    status: state.filters.status as ReturnStatus | undefined,
    kind: state.filters.kind as ReturnKind | undefined,
    fault: state.filters.fault as ReturnFault | undefined,
    branchId: state.filters.branchId,
    from: state.filters.from,
    to: state.filters.to,
  });
  const summary = useReturnSummary();
  const items = query.data?.items ?? [];
  const canBulk = can("returns.return.approve");

  const toggle = React.useCallback((id: string, on: boolean) => {
    setSelected((old) => {
      const next = new Set(old);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }, []);
  const pageIds = items.map((i) => i.id);
  const allOnPage = pageIds.length > 0 && pageIds.every((id) => selected.has(id));
  const someOnPage = pageIds.some((id) => selected.has(id));

  const columns = React.useMemo<ColumnDef<ReturnListItem, unknown>[]>(
    () => [
      ...(canBulk
        ? ([
            {
              id: "select",
              header: () => (
                <Checkbox
                  aria-label={t("selectAll")}
                  checked={allOnPage ? true : someOnPage ? "indeterminate" : false}
                  onCheckedChange={(v) => setSelected((old) => {
                    const next = new Set(old);
                    for (const id of pageIds) {
                      if (v === true) next.add(id);
                      else next.delete(id);
                    }
                    return next;
                  })}
                />
              ),
              enableSorting: false,
              meta: { label: t("selectColumn"), hideOnMobile: false },
              cell: ({ row }) => <Checkbox aria-label={t("selectRow", { number: row.original.number })} checked={selected.has(row.original.id)} onCheckedChange={(v) => toggle(row.original.id, v === true)} />,
            },
          ] as ColumnDef<ReturnListItem, unknown>[])
        : []),
      {
        id: "number",
        header: t("columns.number"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/returns/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
            <span className="font-mono">{row.original.number}</span>
          </Link>
        ),
      },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <ReturnStatusBadge status={row.original.status} /> },
      {
        id: "customer",
        header: t("columns.customer"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block [overflow-wrap:anywhere]">
            <span className="block font-medium text-strong">{row.original.customerName ?? "-"}</span>
            <span className="block text-sm text-muted">{row.original.customerMobile}</span>
          </span>
        ),
      },
      {
        id: "order",
        header: t("columns.order"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <span className="font-mono text-sm text-muted">{row.original.orderNumber}</span>,
      },
      {
        id: "kind",
        header: t("columns.kind"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => <Badge variant="outline">{tk(row.original.kind)}</Badge>,
      },
      {
        id: "reason",
        header: t("columns.reason"),
        enableSorting: false,
        meta: { hideOnMobile: true },
        cell: ({ row }) => (
          <span className="block text-sm [overflow-wrap:anywhere]">
            {reasonLabel(row.original.reasonCode)}
            <span className="block text-muted">{tf(row.original.fault)}</span>
          </span>
        ),
      },
      { id: "quantity", header: t("columns.quantity"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.quantity)}</span> },
      { id: "refund", header: t("columns.refund"), enableSorting: false, meta: { numeric: true }, cell: ({ row }) => <span className="font-medium tabular-nums">{money(row.original.refund)}</span> },
      { id: "branch", header: t("columns.branch"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.branchCode}</span> },
      { id: "requestedAt", header: t("columns.requested"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.requestedAt)}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/returns/${row.original.id}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    // eslint-disable-next-line react-hooks/exhaustive-deps -- pageIds is derived from `items`, which is the data the table already re-renders on
    [t, tk, tf, dateTime, money, reasonLabel, canBulk, selected, allOnPage, someOnPage, items],
  );

  const filtersActive = !!(state.searchText || Object.values(state.filters).some(Boolean));
  const s = summary.data;
  const chipActive = (chip: (typeof SUMMARY_CHIPS)[number]) => !!chip.status && state.filters.status === chip.status && (chip.kind ? state.filters.kind === chip.kind : !state.filters.kind);

  return (
    <div className="space-y-4">
      {summary.isPending ? (
        <Skeleton className="h-20" aria-busy="true" />
      ) : s ? (
        <ul className="grid grid-cols-2 gap-2 md:grid-cols-4 xl:grid-cols-7" aria-label={t("summary")} data-testid="return-summary">
          {SUMMARY_CHIPS.map((chip) => {
            const count = toNumber(s[chip.key]);
            const body = (
              <>
                <span className="block text-2xl font-bold tabular-nums text-strong">{count}</span>
                <span className="block text-sm text-muted">{t(`chips.${chip.key}`)}</span>
              </>
            );
            return (
              <li key={chip.key}>
                {chip.status ? (
                  <button
                    type="button"
                    aria-pressed={chipActive(chip)}
                    data-testid={`chip-${chip.key}`}
                    onClick={() => {
                      const on = chipActive(chip);
                      state.setFilter("status", on ? undefined : chip.status);
                      state.setFilter("kind", on ? undefined : chip.kind);
                    }}
                    className={cn("min-h-11 w-full rounded-card border bg-card p-3 text-start transition-colors", chipActive(chip) ? "border-primary bg-primary-soft" : "border-line [@media(hover:hover)]:hover:border-strong/30")}
                  >
                    {body}
                  </button>
                ) : (
                  <div className="min-h-11 rounded-card border border-line bg-sunken p-3" data-testid={`chip-${chip.key}`}>
                    {body}
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      ) : null}

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2 xl:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={RETURN_STATUSES.map((x) => ({ value: x, label: ts(x) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.kind} onChange={(v) => state.setFilter("kind", v)} options={RETURN_KINDS.map((x) => ({ value: x, label: tk(x) }))} allLabel={t("anyKind")} label={t("columns.kind")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.fault} onChange={(v) => state.setFilter("fault", v)} options={RETURN_FAULTS.map((x) => ({ value: x, label: tf(x) }))} allLabel={t("anyFault")} label={t("fault")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {branches.length > 1 ? (
          <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={t("from")} htmlFor="rq-from">
          <Input id="rq-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="rq-to">
          <Input id="rq-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>

      <Can permission="returns.return.approve">
        {selected.size > 0 ? <BulkBar ids={[...selected]} onDone={() => setSelected(new Set())} /> : null}
      </Can>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      <DataTable
        caption={t("title")}
        columns={columns}
        data={items}
        loading={query.isLoading}
        getRowId={(x) => x.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
      />
    </div>
  );
}

/** Approve or reject the ticked returns in one go; each return is checked on its own, so the answer lists what worked and what did not. */
function BulkBar({ ids, onDone }: { ids: string[]; onDone: () => void }) {
  const t = useTranslations("returns.bulk");
  const tc = useTranslations("returns.common");
  const problemMessage = useProblemMessage();
  const { can } = usePermissions();
  const run = useBulkReturns();
  const [confirm, setConfirm] = React.useState<"approve" | "reject" | null>(null);
  const [reason, setReason] = React.useState("");
  const [result, setResult] = React.useState<BulkResult | null>(null);
  const [reasonError, setReasonError] = React.useState(false);

  const go = async () => {
    if (confirm === "reject" && reason.trim() === "") {
      setReasonError(true);
      return;
    }
    try {
      const answer = await run.mutateAsync({ action: confirm!, ids, reason });
      setResult(answer);
      setConfirm(null);
      setReason("");
      setReasonError(false);
      onDone();
      if (toNumber(answer.failed) === 0) toast.success(t("allDone", { count: toNumber(answer.succeeded) }));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      setConfirm(null);
    }
  };

  return (
    <>
      <div className="flex flex-wrap items-center gap-2 rounded-card border border-primary/30 bg-primary-soft p-3" role="region" aria-label={t("label")} data-testid="bulk-bar">
        <p className="me-auto font-semibold text-strong" aria-live="polite">
          {t("selected", { count: ids.length })}
        </p>
        <Button onClick={() => setConfirm("approve")}>{t("approve")}</Button>
        {can("returns.return.reject") ? (
          <Button variant="secondary" onClick={() => setConfirm("reject")}>
            {t("reject")}
          </Button>
        ) : null}
        <Button variant="ghost" onClick={onDone}>
          {t("clear")}
        </Button>
      </div>

      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => {
          if (!open) {
            setConfirm(null);
            setReason("");
            setReasonError(false);
          }
        }}
        title={confirm === "reject" ? t("rejectTitle", { count: ids.length }) : t("approveTitle", { count: ids.length })}
        description={confirm === "reject" ? t("rejectDescription") : t("approveDescription")}
        confirmLabel={confirm === "reject" ? t("reject") : t("approve")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={confirm === "reject"}
        loading={run.isPending}
        onConfirm={go}
      >
        {confirm === "reject" ? (
          <Field label={t("reason")} htmlFor="bulk-reason" required hint={t("reasonHint")} error={reasonError ? t("reasonRequired") : undefined}>
            <Textarea id="bulk-reason" value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} className="min-h-20" />
          </Field>
        ) : null}
      </ConfirmDialog>

      <Dialog open={result !== null} onOpenChange={(open) => !open && setResult(null)}>
        <ModalContent size="md" closeLabel={tc("close")}>
          <DialogTitle className="font-display text-xl font-semibold text-strong">{t("resultTitle")}</DialogTitle>
          <DialogDescription className="mt-2 text-muted">{t("resultSummary", { ok: toNumber(result?.succeeded), failed: toNumber(result?.failed) })}</DialogDescription>
          <ul className="mt-4 space-y-2" aria-label={t("resultTitle")}>
            {result?.results.map((row) => (
              <li key={row.id} className="flex items-start gap-2 text-sm" data-testid="bulk-result">
                {row.ok ? <CheckCircle2 className="mt-0.5 size-5 shrink-0 text-success" aria-hidden /> : <CircleX className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden />}
                <span className="[overflow-wrap:anywhere]">
                  <span className="font-mono font-semibold text-strong">{row.number ?? row.id}</span>{" "}
                  {row.ok ? t("resultOk") : problemMessage({ code: row.code ?? undefined, status: 409 })}
                </span>
              </li>
            ))}
          </ul>
          <div className="mt-6 flex justify-end">
            <DialogClose asChild>
              <Button variant="secondary">{tc("close")}</Button>
            </DialogClose>
          </div>
        </ModalContent>
      </Dialog>
    </>
  );
}
