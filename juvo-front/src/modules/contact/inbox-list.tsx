"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Lock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Alert } from "@/components/ui/surfaces";
import { SearchBox, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { useMessages, useNewCount, type MessageListItem } from "./api";
import { MessageStatusBadge } from "./common";
import { STATUSES, contactLines, isStatus, type InboxFilters } from "./logic";

/** The staff inbox: status filter with the New count, search, and the table (cards on phones). Opening a message does not change its status. */
export function InboxList() {
  const t = useTranslations("contact.inbox");
  const ts = useTranslations("contact.status");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const state = useListState<InboxFilters & Record<string, string | undefined>>({});
  const query = useMessages({ filters: state.filters, page: state.page, pageSize: state.pageSize, search: state.search });
  const newCount = useNewCount();
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const current = isStatus(state.filters.status) ? state.filters.status : undefined;
  const masked = items.some((i) => i.piiMasked);

  const columns = React.useMemo<ColumnDef<MessageListItem, unknown>[]>(
    () => [
      {
        id: "reference",
        header: t("columns.reference"),
        meta: { primary: true },
        cell: ({ row }) => (
          <Link href={`/admin/content/messages/${row.original.id}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline" data-testid="message-link">
            <span className="font-mono">{row.original.reference}</span>
          </Link>
        ),
      },
      {
        id: "from",
        header: t("columns.from"),
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="block max-w-xs [overflow-wrap:anywhere]">
            <span className="block font-medium text-strong">{row.original.name}</span>
            {contactLines(row.original).map((line) => (
              <span key={line} className="block text-sm text-muted">
                {line}
              </span>
            ))}
          </span>
        ),
      },
      {
        id: "subject",
        header: t("columns.subject"),
        meta: { secondary: true },
        cell: ({ row }) => (
          <span className="block max-w-sm [overflow-wrap:anywhere]">
            <span className={cn("block font-medium", row.original.subject ? "text-strong" : "text-muted")}>{row.original.subject || t("noSubject")}</span>
            <span className="block truncate text-sm text-muted">{row.original.messagePreview}</span>
          </span>
        ),
      },
      { id: "received", header: t("columns.received"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.receivedAt)}</span> },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, cell: ({ row }) => <MessageStatusBadge status={row.original.status} /> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <Button asChild variant="secondary">
            <Link href={`/admin/content/messages/${row.original.id}`} aria-label={t("open", { reference: row.original.reference })}>
              {t("view")}
            </Link>
          </Button>
        ),
      },
    ],
    [t, dateTime],
  );

  const chip = (active: boolean) =>
    cn("inline-flex min-h-11 items-center gap-2 rounded-full border px-4 text-sm font-medium transition-colors", active ? "border-primary bg-primary-soft text-primary-dark" : "border-line bg-card text-strong [@media(hover:hover)]:hover:border-strong/30");
  const count = newCount.data ?? 0;

  return (
    <div className="space-y-4">
      <p className="max-w-3xl text-muted">{t("subtitle")}</p>
      <div role="group" aria-label={t("status")} className="flex flex-wrap gap-2" data-testid="message-filters">
        <button type="button" aria-pressed={!current} className={chip(!current)} data-testid="filter-all" onClick={() => state.setFilter("status", undefined)}>
          {t("anyStatus")}
        </button>
        {STATUSES.map((status) => (
          <button key={status} type="button" aria-pressed={current === status} className={chip(current === status)} data-testid={`filter-${status}`} onClick={() => state.setFilter("status", current === status ? undefined : status)}>
            {status === "New" && count > 0 ? t("statusWithNew", { status: ts(status), count }) : ts(status)}
          </button>
        ))}
      </div>
      <SearchBox className="max-w-xl" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
      {masked ? (
        <Alert variant="info" data-testid="pii-note">
          <span className="flex items-start gap-2">
            <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("piiNote")}</span>
          </span>
        </Alert>
      ) : null}
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("table")}
        columns={columns}
        data={items}
        loading={query.isLoading}
        getRowId={(x) => x.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={Number(query.data?.total ?? 0)}
        onPageChange={state.setPage}
      />
    </div>
  );
}
