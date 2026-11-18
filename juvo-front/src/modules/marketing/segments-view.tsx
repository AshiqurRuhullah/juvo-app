"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Check, Copy, Lock, Pencil, Plus, Trash2, Users } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCopySegment, useDeleteSegment, useSegmentCount, useSegmentMembers, useSegments, type Segment, type SegmentMember } from "./api";
import { useMarketingPermissions } from "./common";
import { ruleSummary } from "./logic";

// The rule builder is only fetched when someone opens it.
const SegmentEditorDialog = dynamic(() => import("./segment-editor"), { ssr: false });

type Editing = { segment: Segment | null } | null;

/** Segments (FR-AC01): the saved and built-in segments with their live sizes, the rule builder, the member list, copy and delete. */
export function SegmentsView() {
  const t = useTranslations("marketing.segments");
  const labels = useTableLabels();
  const problemMessage = useProblemMessage();
  const perms = useMarketingPermissions();
  const state = useListState({});
  const query = useSegments({ page: state.page, pageSize: state.pageSize, search: state.search });
  const copy = useCopySegment();
  const remove = useDeleteSegment();
  const [editing, setEditing] = React.useState<Editing>(null);
  const [members, setMembers] = React.useState<Segment | null>(null);
  const [deleting, setDeleting] = React.useState<Segment | null>(null);
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);

  const makeCopy = React.useCallback(
    async (segment: Segment) => {
      try {
        const made = await copy.mutateAsync(segment.id);
        toast.success(t("copied", { name: made.name }));
      } catch (e) {
        toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      }
    },
    [copy, problemMessage, t],
  );

  const columns = React.useMemo<ColumnDef<Segment, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block max-w-xs [overflow-wrap:anywhere]">
            <span className="flex flex-wrap items-center gap-2">
              <span className="font-semibold text-strong" data-testid="segment-name">
                {row.original.name}
              </span>
              {row.original.isBuiltIn ? (
                <Badge variant="outline">
                  <Lock className="size-3.5" aria-hidden /> {t("builtIn")}
                </Badge>
              ) : null}
            </span>
            {row.original.description ? <span className="block text-sm text-muted">{row.original.description}</span> : null}
          </span>
        ),
      },
      { id: "rules", header: t("columns.rules"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <RuleSummaryList segment={row.original} /> },
      { id: "members", header: t("columns.members"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <CountCell id={row.original.id} part="total" /> },
      { id: "sms", header: t("columns.sms"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <CountCell id={row.original.id} part="sms" /> },
      { id: "email", header: t("columns.email"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <CountCell id={row.original.id} part="email" /> },
      {
        id: "actions",
        header: "",
        enableSorting: false,
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => {
          const s = row.original;
          return (
            <span className="flex flex-col items-stretch gap-2 md:flex-row md:flex-wrap md:justify-end">
              <Button variant="secondary" onClick={() => setMembers(s)} aria-label={t("membersOf", { name: s.name })} data-testid="segment-members">
                <Users aria-hidden /> {t("members")}
              </Button>
              {s.isBuiltIn || !perms.segmentEdit ? (
                <Button variant="secondary" onClick={() => setEditing({ segment: s })} aria-label={t("viewRules", { name: s.name })}>
                  {t("view")}
                </Button>
              ) : (
                <Button variant="secondary" onClick={() => setEditing({ segment: s })} aria-label={t("editOf", { name: s.name })} data-testid="segment-edit">
                  <Pencil aria-hidden /> {t("edit")}
                </Button>
              )}
              {perms.segmentEdit ? (
                <Button variant="secondary" onClick={() => void makeCopy(s)} aria-label={t("copyOf", { name: s.name })} data-testid="segment-copy">
                  <Copy aria-hidden /> {t("copy")}
                </Button>
              ) : null}
              {perms.segmentEdit && !s.isBuiltIn ? (
                <Button variant="ghost" onClick={() => setDeleting(s)} aria-label={t("deleteOf", { name: s.name })} data-testid="segment-delete">
                  <Trash2 aria-hidden /> {t("delete")}
                </Button>
              ) : null}
            </span>
          );
        },
      },
    ],
    [t, perms.segmentEdit, makeCopy],
  );

  const doDelete = async () => {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast.success(t("deleted", { name: deleting.name }));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
    setDeleting(null);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="max-w-2xl text-muted">{t("subtitle")}</p>
        {perms.segmentEdit ? (
          <Button onClick={() => setEditing({ segment: null })} data-testid="new-segment">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>
      <SearchBox className="max-w-md" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
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
      {editing ? <SegmentEditorDialog key={editing.segment?.id ?? "new"} open onOpenChange={(open) => !open && setEditing(null)} segment={editing.segment} /> : null}
      {members ? <MembersDialog segment={members} onClose={() => setMembers(null)} /> : null}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("deleteTitle", { name: deleting?.name ?? "" })}
        description={t("deleteDescription")}
        confirmLabel={t("delete")}
        cancelLabel={t("cancel")}
        closeLabel={t("close")}
        destructive
        loading={remove.isPending}
        onConfirm={doDelete}
      />
    </div>
  );
}

function RuleSummaryList({ segment }: { segment: Segment }) {
  const t = useTranslations("marketing.segments");
  const ts = useTranslations("marketing.segments.summary");
  const rules = segment.definition.rules ?? [];
  const joiner = segment.definition.combinator === "or" ? t("joinOr") : t("joinAnd");
  return (
    <ul className="max-w-sm space-y-0.5 text-sm text-muted">
      {rules.map((rule, i) => {
        const s = ruleSummary(rule);
        const text = s.key === "lastOrderStatus" ? ts("lastOrderStatus", { status: ts(`orderStatus.${String(s.values.status)}`) }) : ts(s.key, s.values);
        return (
          <li key={i}>
            {i > 0 ? <span className="me-1 font-semibold uppercase">{joiner}</span> : null}
            {text}
          </li>
        );
      })}
    </ul>
  );
}

/** One of the live numbers of a segment. The server counts with one set-based query per segment. */
function CountCell({ id, part }: { id: string; part: "total" | "sms" | "email" }) {
  const t = useTranslations("marketing.segments");
  const count = useSegmentCount(id);
  if (count.isPending) return <span className="text-muted" aria-busy="true">…</span>;
  if (count.isError || !count.data) return <span className="text-muted">{t("countFailed")}</span>;
  const value = part === "total" ? count.data.total : count.data.reachable[part];
  return (
    <span className="tabular-nums text-strong" data-testid={`segment-count-${part}`}>
      {toNumber(value)}
    </span>
  );
}

/** The members of a segment, paged. Mobile and email arrive masked when the staff member may not see personal data: shown as given. */
function MembersDialog({ segment, onClose }: { segment: Segment; onClose: () => void }) {
  const t = useTranslations("marketing.segments");
  const tc = useTranslations("marketing.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const money = useMoney();
  const state = useListState({}, undefined, [{ id: "name", desc: false }]);
  const query = useSegmentMembers(segment.id, { page: state.page, pageSize: state.pageSize, sort: state.sort, search: state.search });
  const count = useSegmentCount(segment.id);

  const columns = React.useMemo<ColumnDef<SegmentMember, unknown>[]>(
    () => [
      { id: "name", header: t("member.name"), meta: { primary: true }, cell: ({ row }) => <span className="font-medium text-strong [overflow-wrap:anywhere]">{row.original.name ?? "-"}</span> },
      { id: "contact", header: t("member.contact"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => (
        <span className="block text-sm [overflow-wrap:anywhere]">
          <span className="block">{row.original.mobile}</span>
          <span className="block text-muted">{row.original.email ?? "-"}</span>
        </span>
      ) },
      { id: "orders", header: t("member.orders"), enableSorting: false, meta: { secondary: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.orderCount)}</span> },
      { id: "spend", header: t("member.spend"), meta: { secondary: true, numeric: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.totalSpend)}</span> },
      { id: "lastOrder", header: t("member.lastOrder"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.lastOrderAt)}</span> },
      {
        id: "reach",
        header: t("member.reach"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="flex flex-wrap gap-1">
            <Badge variant={row.original.smsReachable ? "success" : "outline"}>
              {row.original.smsReachable ? <Check className="size-3.5" aria-hidden /> : null} {row.original.smsReachable ? t("member.smsYes") : t("member.smsNo")}
            </Badge>
            <Badge variant={row.original.emailReachable ? "success" : "outline"}>
              {row.original.emailReachable ? <Check className="size-3.5" aria-hidden /> : null} {row.original.emailReachable ? t("member.emailYes") : t("member.emailNo")}
            </Badge>
          </span>
        ),
      },
    ],
    [t, dateTime, money],
  );

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <ModalContent size="lg" closeLabel={tc("close")} data-testid="members-dialog">
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("membersTitle", { name: segment.name })}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">
          {count.data ? t("membersSummary", { total: toNumber(count.data.total), sms: toNumber(count.data.reachable.sms), email: toNumber(count.data.reachable.email) }) : t("membersHint")}
        </DialogDescription>
        <div className="mt-4 space-y-3">
          <SearchBox value={state.searchText} onChange={state.setSearchText} placeholder={t("member.searchPlaceholder")} label={t("member.search")} />
          <p className="text-sm text-muted">{t("member.maskedHint")}</p>
          {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
          <DataTable
            caption={t("membersTitle", { name: segment.name })}
            columns={columns}
            data={query.data?.items ?? []}
            loading={query.isLoading}
            getRowId={(x) => x.customerId}
            labels={labels}
            page={state.page}
            pageSize={state.pageSize}
            total={toNumber(query.data?.total)}
            onPageChange={state.setPage}
            sorting={state.sorting}
            onSortingChange={state.setSorting}
          />
        </div>
      </ModalContent>
    </Dialog>
  );
}
