"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Ban, CircleCheck, Clock, Undo2 } from "lucide-react";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Badge } from "@/components/ui/surfaces";
import { FilterSelect, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useReferrals, type ReferralListItem } from "./api";
import { REFERRAL_STATUSES } from "./logic";

const ICON = { Pending: Clock, Rewarded: CircleCheck, Rejected: Ban, Reversed: Undo2 } as const;
const TONE = { Pending: "warning", Rewarded: "success", Rejected: "danger", Reversed: "neutral" } as const;

/** Badge with icon and word, so the colour is never the only signal (a rejected referral is crimson with an icon). */
function StatusBadge({ status }: { status: ReferralListItem["status"] }) {
  const t = useTranslations("loyalty.referrals.status");
  if (!status) return null;
  const Icon = ICON[status];
  return (
    <Badge variant={TONE[status]} data-testid="referral-status">
      <Icon className="size-3.5" aria-hidden /> {t(status)}
    </Badge>
  );
}

/** Staff `Referrals` tab: who referred whom, the state and, for a rejected one, which fraud rule decided (the API's note). */
export default function ReferralsView() {
  const t = useTranslations("loyalty.referrals");
  const tc = useTranslations("loyalty.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const state = useListState<{ status?: string }>({ status: undefined });
  const query = useReferrals({ page: state.page, pageSize: state.pageSize, status: state.filters.status });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);

  const columns = React.useMemo<ColumnDef<ReferralListItem, unknown>[]>(
    () => [
      { id: "referee", header: t("columns.referee"), meta: { primary: true }, cell: ({ row }) => <span className="font-semibold text-strong">{row.original.refereeName ?? "-"}</span> },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, cell: ({ row }) => <StatusBadge status={row.original.status} /> },
      { id: "referrer", header: t("columns.referrer"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{t("by", { name: row.original.referrerName ?? "-" })}</span> },
      { id: "code", header: t("columns.code"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="font-mono text-sm">{row.original.codeUsed}</span> },
      {
        id: "outcome",
        header: t("columns.outcome"),
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.note ? (
            <span className="text-sm text-muted [overflow-wrap:anywhere]" data-testid="referral-note">
              {row.original.note}
            </span>
          ) : (
            <span className="text-sm text-muted">-</span>
          ),
      },
      {
        id: "rewards",
        header: t("columns.rewards"),
        meta: { align: "end", hideOnMobile: true },
        cell: ({ row }) => (
          <span className="text-sm tabular-nums text-muted">
            {toNumber(row.original.referrerReward) > 0 || toNumber(row.original.refereeReward) > 0 ? `${money(row.original.referrerReward)} / ${money(row.original.refereeReward)}` : "-"}
          </span>
        ),
      },
      { id: "applied", header: t("columns.applied"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.appliedAt)}</span> },
      { id: "decided", header: t("columns.decided"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{row.original.decidedAt ? dateTime(row.original.decidedAt) : "-"}</span> },
    ],
    [t, money, dateTime],
  );

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted">{t("help")}</p>
      <div className="w-full sm:w-64">
        <FilterSelect
          value={state.filters.status}
          onChange={(v) => state.setFilter("status", v)}
          options={REFERRAL_STATUSES.map((s) => ({ value: s, label: t(`status.${s}`) }))}
          allLabel={t("anyStatus")}
          label={t("columns.status")}
          searchPlaceholder={tc("search")}
          emptyText={tc("noMatch")}
        />
      </div>
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} page={state.page} pageSize={state.pageSize} total={toNumber(query.data?.total)} onPageChange={state.setPage} />
    </div>
  );
}
