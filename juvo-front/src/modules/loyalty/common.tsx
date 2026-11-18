"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Badge } from "@/components/ui/surfaces";
import { DataTable } from "@/components/ui/data-table";
import { useDateTime, useTableLabels } from "@/components/data/list-kit";
import { useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { canAnywhere } from "@/lib/auth/permissions";
import { toNumber, useMoney } from "@/modules/catalog/format";
import type { CreditEntry, PointsEntry } from "./api";
import { ledgerTone, signed } from "./logic";

/** What this staff member may do with rewards. Display only: the API still refuses what is not allowed. */
export function useLoyaltyPermissions() {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      pointsView: canAnywhere(me, "loyalty.points.view"),
      pointsAdjust: canAnywhere(me, "loyalty.points.adjust"),
      cardView: canAnywhere(me, "loyalty.gift-card.view"),
      cardCreate: canAnywhere(me, "loyalty.gift-card.create"),
      referralView: canAnywhere(me, "loyalty.referral.view"),
      customerView: canAnywhere(me, "customers.customer.view"),
    }),
    [me],
  );
}

/** Friendly text for a failed rewards call (errors.LOYALTY_* and the general ones). */
export function useLoyaltyErrorText() {
  const message = useProblemMessage();
  return (error: unknown): string => message(error instanceof ApiError ? error.problem : null);
}

export const problemCodeOf = (error: unknown): string | undefined => (error instanceof ApiError ? error.problem.code : undefined);

/** The tender problem codes the quote can carry, as the text key under errors.* (LOYALTY.TENDER_EXCEEDS_CAP gives LOYALTY_TENDER_EXCEEDS_CAP). */
export const errorKey = (code: string): string => code.replaceAll(".", "_");

/** Money with an explicit sign for ledger lines. */
function useSignedMoney() {
  const money = useMoney();
  return (value: number) => signed(value, (n) => money(n));
}

type PagedLedger<T> = { items: T[]; total: number | string } | undefined;

/** The points ledger as a table (cards on phones). Used by the customer page and the staff lookup. */
export function PointsLedgerTable({ data, loading, page, onPageChange, caption }: { data: PagedLedger<PointsEntry>; loading: boolean; page: number; onPageChange: (page: number) => void; caption: string }) {
  const t = useTranslations("loyalty.ledger");
  const tk = useTranslations("loyalty.ledger.pointsKind");
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const items = React.useMemo(() => data?.items ?? [], [data]);

  const columns = React.useMemo<ColumnDef<PointsEntry, unknown>[]>(
    () => [
      {
        id: "kind",
        header: t("columns.kind"),
        meta: { primary: true },
        cell: ({ row }) => <Badge variant={ledgerTone(toNumber(row.original.points)) === "danger" ? "danger" : ledgerTone(toNumber(row.original.points)) === "success" ? "success" : "neutral"}>{tk(row.original.kind)}</Badge>,
      },
      {
        id: "points",
        header: t("columns.points"),
        meta: { align: "end", secondary: true },
        cell: ({ row }) => (
          <span className="font-semibold tabular-nums text-strong" data-testid="ledger-amount">
            {signed(toNumber(row.original.points))}
          </span>
        ),
      },
      { id: "balance", header: t("columns.balance"), meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="tabular-nums text-muted">{toNumber(row.original.balanceAfter)}</span> },
      { id: "reason", header: t("columns.reason"), meta: { secondary: true }, cell: ({ row }) => <span className="text-sm text-muted [overflow-wrap:anywhere]">{row.original.reason}</span> },
      { id: "order", header: t("columns.order"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.orderNumber ?? "-"}</span> },
      { id: "expires", header: t("columns.expires"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-sm text-muted">{row.original.expiresAt ? dateTime(row.original.expiresAt) : "-"}</span> },
      { id: "at", header: t("columns.at"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.at)}</span> },
    ],
    [t, tk, dateTime],
  );

  return <DataTable caption={caption} columns={columns} data={items} loading={loading} getRowId={(x) => x.id} labels={labels} page={page} total={toNumber(data?.total)} onPageChange={onPageChange} />;
}

/** The store credit ledger as a table (cards on phones). */
export function CreditLedgerTable({ data, loading, page, onPageChange, caption }: { data: PagedLedger<CreditEntry>; loading: boolean; page: number; onPageChange: (page: number) => void; caption: string }) {
  const t = useTranslations("loyalty.ledger");
  const tk = useTranslations("loyalty.ledger.creditKind");
  const money = useMoney();
  const signedMoney = useSignedMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const items = React.useMemo(() => data?.items ?? [], [data]);

  const columns = React.useMemo<ColumnDef<CreditEntry, unknown>[]>(
    () => [
      {
        id: "kind",
        header: t("columns.kind"),
        meta: { primary: true },
        cell: ({ row }) => <Badge variant={ledgerTone(toNumber(row.original.amount)) === "danger" ? "danger" : ledgerTone(toNumber(row.original.amount)) === "success" ? "success" : "neutral"}>{tk(row.original.kind)}</Badge>,
      },
      {
        id: "amount",
        header: t("columns.amount"),
        meta: { align: "end", secondary: true },
        cell: ({ row }) => (
          <span className="font-semibold tabular-nums text-strong" data-testid="ledger-amount">
            {signedMoney(toNumber(row.original.amount))}
          </span>
        ),
      },
      { id: "balance", header: t("columns.balance"), meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="tabular-nums text-muted">{money(row.original.balanceAfter)}</span> },
      { id: "reason", header: t("columns.reason"), meta: { secondary: true }, cell: ({ row }) => <span className="text-sm text-muted [overflow-wrap:anywhere]">{row.original.reason}</span> },
      { id: "order", header: t("columns.order"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.orderNumber ?? "-"}</span> },
      { id: "at", header: t("columns.at"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.at)}</span> },
    ],
    [t, tk, money, signedMoney, dateTime],
  );

  return <DataTable caption={caption} columns={columns} data={items} loading={loading} getRowId={(x) => x.id} labels={labels} page={page} total={toNumber(data?.total)} onPageChange={onPageChange} />;
}
