"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { CheckCircle2, CircleX, Clock, Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, Field, Input, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { cn } from "@/lib/utils";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { useBulkTickets, useTicketSummary, useTickets, type BulkResult, type TicketListItem } from "./api";
import { PriorityBadge, StatusBadge, useAssigneeOptions, useCategoryLabel, useCategoryOptions, useSupportPermissions } from "./common";
import { BULK_LIMIT, TICKET_CHANNELS, TICKET_PRIORITIES, TICKET_STATUSES, type TicketFilters } from "./logic";

// The "new ticket" dialog is only fetched when someone opens it.
const CreateTicketDialog = dynamic(() => import("./create-ticket-dialog"), { ssr: false });

const NONE = "__none";
type Chip = "unassigned" | "overdue" | "mine";
const EXTRA_CHIPS: Chip[] = ["unassigned", "overdue", "mine"];

/** The staff ticket list: counts on top, filters, the table (cards on phones) and the bulk assign / close bar. */
export function TicketsList() {
  const t = useTranslations("support.list");
  const ts = useTranslations("support.status");
  const tp = useTranslations("support.priority");
  const tch = useTranslations("support.channel");
  const tc = useTranslations("support.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const categoryLabel = useCategoryLabel();
  const categoryOptions = useCategoryOptions();
  const branches = useBranchOptions();
  const perms = useSupportPermissions();
  const state = useListState<TicketFilters>({}, undefined, [{ id: "activity", desc: true }]);
  const assignees = useAssigneeOptions(true);
  const [selected, setSelected] = React.useState<ReadonlySet<string>>(new Set());
  const [creating, setCreating] = React.useState(false);

  const query = useTickets({ filters: state.filters, page: state.page, pageSize: state.pageSize, search: state.search, sort: state.sort, me: perms.userId });
  const summary = useTicketSummary();
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const canBulk = perms.assign || perms.close;
  // Ticket numbers of everything seen, so the bulk answer can name tickets from other pages too.
  const numbers = React.useRef<Record<string, string>>({});
  React.useEffect(() => {
    for (const i of items) numbers.current[i.id] = i.number;
  }, [items]);

  const toggle = React.useCallback((id: string, on: boolean) => {
    setSelected((old) => {
      const next = new Set(old);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }, []);
  const pageIds = React.useMemo(() => items.map((i) => i.id), [items]);
  const allOnPage = pageIds.length > 0 && pageIds.every((id) => selected.has(id));
  const someOnPage = pageIds.some((id) => selected.has(id));

  const columns = React.useMemo<ColumnDef<TicketListItem, unknown>[]>(
    () => [
      ...(canBulk
        ? ([
            {
              id: "select",
              header: () => (
                <label className="-m-2 inline-flex size-11 cursor-pointer items-center justify-center">
                  <Checkbox
                    aria-label={t("selectAll")}
                    checked={allOnPage ? true : someOnPage ? "indeterminate" : false}
                    onCheckedChange={(v) =>
                      setSelected((old) => {
                        const next = new Set(old);
                        for (const id of pageIds) {
                          if (v === true) next.add(id);
                          else next.delete(id);
                        }
                        return next;
                      })
                    }
                  />
                </label>
              ),
              enableSorting: false,
              meta: { label: t("selectColumn"), hideOnMobile: false },
              cell: ({ row }) => (
                <label className="-m-2 inline-flex size-11 cursor-pointer items-center justify-center">
                  <Checkbox aria-label={t("selectRow", { number: row.original.number })} checked={selected.has(row.original.id)} onCheckedChange={(v) => toggle(row.original.id, v === true)} />
                </label>
              ),
            },
          ] as ColumnDef<TicketListItem, unknown>[])
        : []),
      {
        id: "number",
        header: t("columns.number"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/support/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline" data-testid="ticket-link">
            <span className="font-mono">{row.original.number}</span>
          </Link>
        ),
      },
      {
        id: "subject",
        header: t("columns.subject"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block max-w-xs [overflow-wrap:anywhere]">
            <span className="block font-medium text-strong">{row.original.subject}</span>
            {row.original.orderNumber ? <span className="block font-mono text-xs text-muted">{row.original.orderNumber}</span> : null}
          </span>
        ),
      },
      {
        id: "status",
        header: t("columns.status"),
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="flex flex-wrap items-center gap-1">
            <StatusBadge status={row.original.status} />
            {row.original.overdue ? (
              <Badge variant="danger" data-testid="ticket-overdue">
                <Clock className="size-3.5" aria-hidden /> {t("overdue")}
              </Badge>
            ) : null}
          </span>
        ),
      },
      { id: "priority", header: t("columns.priority"), meta: { secondary: true }, cell: ({ row }) => <PriorityBadge priority={row.original.priority} /> },
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
      { id: "category", header: t("columns.category"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <Badge variant="outline">{categoryLabel(row.original.category)}</Badge> },
      {
        id: "assignee",
        header: t("columns.assignee"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <span className={row.original.assigneeName ? "text-strong" : "text-muted"}>{row.original.assigneeName ?? t("unassigned")}</span>,
      },
      { id: "channel", header: t("columns.channel"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-sm text-muted">{tch(row.original.channel)}</span> },
      { id: "activity", header: t("columns.activity"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.lastActivityAt)}</span> },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/support/${row.original.id}`} aria-label={t("open", { number: row.original.number })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, tch, dateTime, categoryLabel, canBulk, selected, allOnPage, someOnPage, pageIds, toggle],
  );

  const s = summary.data;
  const chip = state.filters.chip as Chip | undefined;
  const statusActive = state.filters.status;
  const filtersActive = !!(state.searchText || Object.values(state.filters).some(Boolean));

  const pickStatus = (status: string) => {
    const on = statusActive === status && !chip;
    state.setFilter("chip", undefined);
    state.setFilter("status", on ? undefined : status);
  };
  const pickChip = (c: Chip) => {
    const on = chip === c;
    state.setFilter("status", undefined);
    state.setFilter("chip", on ? undefined : c);
  };
  const chipClass = (active: boolean) => cn("min-h-11 w-full rounded-card border bg-card p-3 text-start transition-colors", active ? "border-primary bg-primary-soft" : "border-line [@media(hover:hover)]:hover:border-strong/30");
  const count = (value: number | string | undefined) => toNumber(value);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-muted">{t("subtitle")}</p>
        {perms.create ? (
          <Button onClick={() => setCreating(true)} data-testid="new-ticket">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>

      {summary.isPending ? (
        <Skeleton className="h-20" aria-busy="true" />
      ) : s ? (
        <ul className="grid grid-cols-2 gap-2 md:grid-cols-4 xl:grid-cols-8" aria-label={t("summary")} data-testid="ticket-summary">
          {TICKET_STATUSES.map((status) => (
            <li key={status}>
              <button type="button" aria-pressed={statusActive === status && !chip} data-testid={`chip-${status}`} onClick={() => pickStatus(status)} className={chipClass(statusActive === status && !chip)}>
                <span className="block text-2xl font-bold tabular-nums text-strong">{count(s.byStatus[status])}</span>
                <span className="block text-sm text-muted">{ts(status)}</span>
              </button>
            </li>
          ))}
          {EXTRA_CHIPS.map((c) => (
            <li key={c}>
              <button type="button" aria-pressed={chip === c} data-testid={`chip-${c}`} onClick={() => pickChip(c)} className={chipClass(chip === c)}>
                <span className="block text-2xl font-bold tabular-nums text-strong">{count(s[c])}</span>
                <span className="block text-sm text-muted">{t(`chips.${c}`)}</span>
              </button>
            </li>
          ))}
        </ul>
      ) : null}

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <SearchBox className="sm:col-span-2" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
        <FilterSelect value={state.filters.status} onChange={(v) => state.setFilter("status", v)} options={TICKET_STATUSES.map((x) => ({ value: x, label: ts(x) }))} allLabel={t("anyStatus")} label={t("columns.status")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.priority} onChange={(v) => state.setFilter("priority", v)} options={TICKET_PRIORITIES.map((x) => ({ value: x, label: tp(x) }))} allLabel={t("anyPriority")} label={t("columns.priority")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.assigneeId} onChange={(v) => state.setFilter("assigneeId", v)} options={[{ value: NONE, label: t("unassigned") }, ...assignees]} allLabel={t("anyAssignee")} label={t("columns.assignee")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.channel} onChange={(v) => state.setFilter("channel", v)} options={TICKET_CHANNELS.map((x) => ({ value: x, label: tch(x) }))} allLabel={t("anyChannel")} label={t("columns.channel")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        <FilterSelect value={state.filters.category} onChange={(v) => state.setFilter("category", v)} options={categoryOptions} allLabel={t("anyCategory")} label={t("columns.category")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        {branches.length > 1 ? (
          <FilterSelect value={state.filters.branchId} onChange={(v) => state.setFilter("branchId", v)} options={branches} allLabel={tc("anyBranch")} label={tc("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        ) : null}
        <Field label={t("orderNumber")} htmlFor="tk-order">
          <Input id="tk-order" value={state.filters.orderNumber ?? ""} onChange={(e) => state.setFilter("orderNumber", e.target.value || undefined)} autoComplete="off" />
        </Field>
        <Field label={t("from")} htmlFor="tk-from">
          <Input id="tk-from" type="date" value={state.filters.from ?? ""} max={state.filters.to || undefined} onChange={(e) => state.setFilter("from", e.target.value || undefined)} />
        </Field>
        <Field label={t("to")} htmlFor="tk-to">
          <Input id="tk-to" type="date" value={state.filters.to ?? ""} min={state.filters.from || undefined} onChange={(e) => state.setFilter("to", e.target.value || undefined)} />
        </Field>
        {filtersActive ? (
          <div className="flex items-end">
            <Button variant="ghost" onClick={state.resetFilters}>
              {tc("clear")}
            </Button>
          </div>
        ) : null}
      </div>

      {selected.size > 0 && canBulk ? <BulkBar ids={[...selected]} numbers={numbers} assignees={assignees} onDone={() => setSelected(new Set())} /> : null}

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
        sorting={state.sorting}
        onSortingChange={state.setSorting}
      />

      {creating ? <CreateTicketDialog open={creating} onOpenChange={setCreating} /> : null}
    </div>
  );
}

/** Assign or close the ticked tickets in one go; each ticket is checked on its own, so the answer lists what worked and what did not. */
function BulkBar({ ids, numbers, assignees, onDone }: { ids: string[]; numbers: React.RefObject<Record<string, string>>; assignees: { value: string; label: string }[]; onDone: () => void }) {
  const t = useTranslations("support.bulk");
  const tc = useTranslations("support.common");
  const problemMessage = useProblemMessage();
  const perms = useSupportPermissions();
  const run = useBulkTickets();
  const [dialog, setDialog] = React.useState<"assign" | "close" | null>(null);
  const [assignee, setAssignee] = React.useState("");
  const [reason, setReason] = React.useState("");
  const [result, setResult] = React.useState<BulkResult | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const over = ids.length > BULK_LIMIT;

  const go = async () => {
    if (dialog === "assign" && !assignee) {
      setError(t("pickPerson"));
      return;
    }
    try {
      const answer = await run.mutateAsync({ action: dialog!, ids, assigneeId: dialog === "assign" ? (assignee === NONE ? null : assignee) : undefined, reason });
      setResult(answer);
      setDialog(null);
      setReason("");
      setError(null);
      if (toNumber(answer.failed) === 0) toast.success(t("allDone", { count: toNumber(answer.succeeded) }));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      setDialog(null);
    }
  };

  return (
    <>
      <div className="flex flex-wrap items-center gap-2 rounded-card border border-primary/30 bg-primary-soft p-3" role="region" aria-label={t("label")} data-testid="bulk-bar">
        <p className="me-auto font-semibold text-strong" aria-live="polite">
          {t("selected", { count: ids.length })}
          {over ? <span className="block text-sm font-normal text-danger-ink">{t("overLimit", { max: BULK_LIMIT })}</span> : null}
        </p>
        {perms.assign ? (
          <Button disabled={over} onClick={() => setDialog("assign")}>
            {t("assign")}
          </Button>
        ) : null}
        {perms.close ? (
          <Button variant="secondary" disabled={over} onClick={() => setDialog("close")}>
            {t("close")}
          </Button>
        ) : null}
        <Button variant="ghost" onClick={onDone}>
          {t("clear")}
        </Button>
      </div>

      <ConfirmDialog
        open={dialog !== null}
        onOpenChange={(open) => {
          if (!open) {
            setDialog(null);
            setReason("");
            setError(null);
          }
        }}
        title={dialog === "close" ? t("closeTitle", { count: ids.length }) : t("assignTitle", { count: ids.length })}
        description={dialog === "close" ? t("closeDescription") : t("assignDescription")}
        confirmLabel={dialog === "close" ? t("close") : t("assign")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={dialog === "close"}
        loading={run.isPending}
        onConfirm={go}
      >
        {dialog === "assign" ? (
          <Field label={t("person")} htmlFor="bulk-person" required error={error ?? undefined}>
            <Select id="bulk-person" value={assignee} onValueChange={setAssignee} options={[{ value: NONE, label: t("unassign") }, ...assignees]} placeholder={t("person")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        ) : (
          <Field label={t("reason")} htmlFor="bulk-reason" hint={t("reasonHint")}>
            <Textarea id="bulk-reason" value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} className="min-h-20" />
          </Field>
        )}
      </ConfirmDialog>

      <Dialog
        open={result !== null}
        onOpenChange={(open) => {
          if (!open) {
            setResult(null);
            onDone();
          }
        }}
      >
        <ModalContent size="md" closeLabel={tc("close")}>
          <DialogTitle className="font-display text-xl font-semibold text-strong">{t("resultTitle")}</DialogTitle>
          <DialogDescription className="mt-2 text-muted">{t("resultSummary", { ok: toNumber(result?.succeeded), failed: toNumber(result?.failed) })}</DialogDescription>
          <ul className="mt-4 max-h-80 space-y-2 overflow-y-auto" aria-label={t("resultTitle")}>
            {result?.items.map((row) => (
              <li key={row.id} className="flex items-start gap-2 text-sm" data-testid="bulk-result">
                {row.ok ? <CheckCircle2 className="mt-0.5 size-5 shrink-0 text-success" aria-hidden /> : <CircleX className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden />}
                <span className="[overflow-wrap:anywhere]">
                  <span className="font-mono font-semibold text-strong">{numbers.current[row.id] ?? row.id}</span> {row.ok ? t("resultOk") : problemMessage({ code: row.code ?? undefined, status: 409 })}
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
