"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { ArrowLeft, Ban, HandCoins, Printer } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Alert, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime, useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { ActionDialog } from "@/modules/payments/action-dialog";
import { usePaymentPermissions } from "@/modules/payments/permissions";
import { useCancelSettlement, useSettlement, useSettleSettlement, type Settlement, type SettlementItem } from "@/modules/couriers/api";
import { useCourierPermissions } from "@/modules/couriers/permissions";
import { Difference, SettlementStatusBadge, useBranchLabel, useMoney2 } from "./common";
import { REFERENCE_MAX, availableActions, num } from "./logic";

// The reconcile form is only needed for open and reconciled batches: it is loaded on demand.
const ReconcileForm = dynamic(() => import("./reconcile-form").then((m) => m.ReconcileForm), { loading: () => <Skeleton className="h-64" aria-busy="true" /> });

/** `/admin/cod/[id]`: one batch with its totals, history, parcels and the steps this person may take. */
export function SettlementDetail({ id }: { id: string }) {
  const t = useTranslations("cod.detail");
  const tc = useTranslations("cod.common");
  const tn = useTranslations("nav");
  const query = useSettlement(id);
  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.codSettlements"), href: "/admin/cod" }, { label: query.data?.number ?? "..." }];

  if (query.isPending) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <div className="space-y-4" aria-busy="true">
          <Skeleton className="h-28" />
          <Skeleton className="h-64" />
        </div>
      </>
    );
  }
  if (query.isError || !query.data) {
    const missing = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <EmptyState
          title={missing ? t("notFound") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/cod">
                <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
              </Link>
            </Button>
          }
        />
      </>
    );
  }
  return <Loaded settlement={query.data} crumbs={crumbs} />;
}

function Loaded({ settlement: s, crumbs }: { settlement: Settlement; crumbs: { label: string; href?: string }[] }) {
  const t = useTranslations("cod.detail");
  const money = useMoney2();
  const branchLabel = useBranchLabel();
  const perms = useCourierPermissions(s.branchId);
  const pay = usePaymentPermissions();
  const actions = availableActions(s.status, perms.codSettle);
  const [settleOpen, setSettleOpen] = React.useState(false);
  const [cancelOpen, setCancelOpen] = React.useState(false);

  return (
    <>
      <PageHeader
        title={s.number}
        description={t("subtitle", { courier: s.courierName, branch: branchLabel(s.branchId).label })}
        crumbs={crumbs}
        actions={
          <>
            <SettlementStatusBadge status={s.status} />
            <Button asChild variant="secondary">
              <Link href={`/admin/cod/${s.id}/print`} data-testid="print-link">
                <Printer aria-hidden /> {t("print")}
              </Link>
            </Button>
            {actions.settle ? (
              <Button onClick={() => setSettleOpen(true)} data-testid="settle">
                <HandCoins aria-hidden /> {t("settle")}
              </Button>
            ) : null}
            {actions.cancel ? (
              <Button variant="secondary" onClick={() => setCancelOpen(true)} data-testid="cancel-batch">
                <Ban aria-hidden /> {t("cancel")}
              </Button>
            ) : null}
          </>
        }
      />

      <div className="space-y-6">
        {s.status === "Settled" ? (
          <Alert variant="success" title={t("settledTitle")} data-testid="settled-note">
            {t("settledBody", { count: s.items.length })}{" "}
            {pay.view ? (
              <Link href="/admin/payments" className="inline-flex min-h-11 items-center font-medium text-primary underline">
                {t("toPayments")}
              </Link>
            ) : null}
          </Alert>
        ) : null}
        {s.status === "Cancelled" ? <Alert variant="info" data-testid="cancelled-note">{t("cancelledBody")}</Alert> : null}
        {s.status === "Reconciled" && perms.codSettle ? <Alert variant="info" data-testid="reconciled-hint">{t("reconciledHint")}</Alert> : null}

        <section aria-label={t("totals")}>
          <dl className="grid grid-cols-2 gap-2 md:grid-cols-3 xl:grid-cols-6" data-testid="totals">
            <Stat label={t("expected")} testId="total-expected">{money(s.expectedTotal)}</Stat>
            <Stat label={t("received")} testId="total-received">{s.receivedTotal === null ? "-" : money(s.receivedTotal)}</Stat>
            <Stat label={t("difference")} testId="total-difference">
              <Difference difference={s.difference} />
            </Stat>
            <Stat label={t("fees")} hint={t("feesHint")} testId="total-fees">{money(s.feesTotal)}</Stat>
            <Stat label={t("shortParcels")} testId="total-short">{num(s.shortParcels)}</Stat>
            <Stat label={t("overParcels")} testId="total-over">{num(s.overParcels)}</Stat>
          </dl>
        </section>

        {actions.reconcile ? <ReconcileForm key={`${s.id}-${s.reconciledAt ?? "open"}`} settlement={s} /> : null}

        <Card>
          <CardHeader>
            <CardTitle>{t("parcels", { count: s.items.length })}</CardTitle>
          </CardHeader>
          <CardContent>
            <ParcelsTable items={s.items} />
          </CardContent>
        </Card>

        <History settlement={s} />
      </div>

      {actions.settle ? <SettleDialog open={settleOpen} onOpenChange={setSettleOpen} settlement={s} /> : null}
      {actions.cancel ? <CancelDialog open={cancelOpen} onOpenChange={setCancelOpen} settlement={s} /> : null}
    </>
  );
}

function Stat({ label, hint, testId, children }: { label: string; hint?: string; testId: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0 rounded-card border border-line bg-card p-3" data-testid={testId}>
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="text-lg font-bold tabular-nums text-strong">{children}</dd>
      {hint ? <p className="text-xs text-muted">{hint}</p> : null}
    </div>
  );
}

function ParcelsTable({ items }: { items: SettlementItem[] }) {
  const t = useTranslations("cod.detail");
  const labels = useTableLabels();
  const money = useMoney2();
  const columns = React.useMemo<ColumnDef<SettlementItem, unknown>[]>(
    () => [
      { id: "consignment", header: t("columns.consignment"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]">{row.original.consignmentId}</span> },
      {
        id: "order",
        header: t("columns.order"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => (
          <Link href={`/admin/orders/${row.original.orderId}`} className="inline-flex min-h-11 items-center rounded-control font-mono text-sm text-primary hover:underline">
            {row.original.orderNumber}
          </Link>
        ),
      },
      { id: "expected", header: t("columns.expected"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.expected)}</span> },
      { id: "received", header: t("columns.received"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="tabular-nums">{money(row.original.received)}</span> },
      { id: "difference", header: t("columns.difference"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <Difference difference={row.original.difference} /> },
      { id: "fee", header: t("columns.fee"), enableSorting: false, meta: { numeric: true, hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums text-muted">{money(row.original.courierFee)}</span> },
      { id: "note", header: t("columns.note"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="block max-w-xs text-sm [overflow-wrap:anywhere]">{row.original.note ?? "-"}</span> },
    ],
    [t, money],
  );
  return <DataTable caption={t("parcelsCaption")} columns={columns} data={items} getRowId={(x) => x.shipmentId} labels={labels} />;
}

/** Who did what and when. The API records the person for creating, reconciling and settling; a cancellation has a time only. */
function History({ settlement: s }: { settlement: Settlement }) {
  const t = useTranslations("cod.detail");
  const dateTime = useDateTime();
  const rows = [
    { key: "created", at: s.createdAt, who: s.createdByName },
    { key: "reconciled", at: s.reconciledAt, who: s.reconciledByName },
    { key: "settled", at: s.settledAt, who: s.settledByName },
    { key: "cancelled", at: s.cancelledAt, who: null },
  ].filter((r) => r.at);
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("history")}</CardTitle>
      </CardHeader>
      <CardContent>
        <ol className="space-y-3" aria-label={t("history")} data-testid="history">
          {rows.map((r) => (
            <li key={r.key} className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-0.5">
              <span className="font-medium text-strong">{t(`events.${r.key}`)}{r.who ? <span className="font-normal text-muted"> · {r.who}</span> : null}</span>
              <span className="text-sm text-muted">{dateTime(r.at)}</span>
            </li>
          ))}
        </ol>
        {s.reference ? (
          <p className="mt-3 text-sm text-muted">
            {t("reference")}: <span className="font-mono font-semibold text-strong" data-testid="reference">{s.reference}</span>
          </p>
        ) : null}
        {s.note ? (
          <p className="mt-1 text-sm text-muted [overflow-wrap:anywhere]">
            {t("note")}: <span className="text-strong">{s.note}</span>
          </p>
        ) : null}
      </CardContent>
    </Card>
  );
}

function SettleDialog({ open, onOpenChange, settlement: s }: { open: boolean; onOpenChange: (open: boolean) => void; settlement: Settlement }) {
  const t = useTranslations("cod.settle");
  const tc = useTranslations("cod.common");
  const money = useMoney2();
  const problemMessage = useProblemMessage();
  const run = useSettleSettlement();
  return (
    <ActionDialog
      open={open}
      onOpenChange={onOpenChange}
      title={t("title", { number: s.number })}
      description={t("description", { count: s.items.length, total: money(s.receivedTotal ?? s.expectedTotal) })}
      confirmLabel={t("confirm")}
      cancelLabel={tc("cancel")}
      closeLabel={tc("close")}
      requiredText={t("referenceRequired")}
      fields={[{ name: "reference", label: t("reference"), required: true, hint: t("referenceHint"), max: REFERENCE_MAX }]}
      onSubmit={async (values) => {
        try {
          await run.mutateAsync({ id: s.id, reference: values.reference! });
          toast.success(t("done"));
          return null;
        } catch (e) {
          return problemMessage(e instanceof ApiError ? e.problem : null);
        }
      }}
    />
  );
}

function CancelDialog({ open, onOpenChange, settlement: s }: { open: boolean; onOpenChange: (open: boolean) => void; settlement: Settlement }) {
  const t = useTranslations("cod.cancel");
  const tc = useTranslations("cod.common");
  const problemMessage = useProblemMessage();
  const run = useCancelSettlement();
  return (
    <ConfirmDialog
      open={open}
      onOpenChange={onOpenChange}
      title={t("title", { number: s.number })}
      description={t("description", { count: s.items.length })}
      confirmLabel={t("confirm")}
      cancelLabel={tc("keep")}
      closeLabel={tc("close")}
      destructive
      loading={run.isPending}
      onConfirm={async () => {
        try {
          await run.mutateAsync(s.id);
          toast.success(t("done"));
          onOpenChange(false);
        } catch (e) {
          toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
          onOpenChange(false);
        }
      }}
    />
  );
}
