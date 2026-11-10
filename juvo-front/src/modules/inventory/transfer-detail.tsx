"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowRight, CheckCircle2, Circle, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Textarea } from "@/components/ui/form";
import { Alert, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useTransfer, useTransferAction, type TransferAction } from "./api";
import { InventoryTabs, TransferStatusBadge, VariantText } from "./common";

type Confirming = "ship" | "reject" | "cancel" | null;

/** One transfer: where it goes, what is on it, how far it got, and the next steps this person may take (the API says which). */
export function TransferDetail({ id }: { id: string }) {
  const t = useTranslations("inventory.transferDetail");
  const tn = useTranslations("nav");
  const tc = useTranslations("inventory.common");
  const dateTime = useDateTime();
  const report = useReportError();
  const query = useTransfer(id);
  const run = useTransferAction(id);
  const [confirming, setConfirming] = React.useState<Confirming>(null);
  const [reason, setReason] = React.useState("");

  const transfer = query.data;
  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.inventory"), href: "/admin/inventory" },
    { label: t("crumb"), href: "/admin/inventory/transfers" },
    { label: transfer?.number ?? "..." },
  ];

  if (query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy="true" />
      </>
    );
  }
  if (query.isError || !transfer) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFoundTitle")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFoundTitle") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/inventory/transfers">{t("back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const allowed = new Set(transfer.allowedActions);
  const act = async (action: TransferAction, withReason = false) => {
    try {
      await run.mutateAsync({ action, reason: withReason ? reason : undefined });
      toast.success(t(`done.${action}`));
      setConfirming(null);
      setReason("");
    } catch (e) {
      report(e);
      setConfirming(null);
    }
  };

  const closedBad = transfer.status === "Cancelled" || transfer.status === "Rejected";
  const steps = [
    { id: "requested", label: t("timeline.requested"), at: transfer.createdAt },
    { id: "approved", label: t("timeline.approved"), at: transfer.approvedAt },
    { id: "shipped", label: t("timeline.shipped"), at: transfer.shippedAt },
    { id: "received", label: t("timeline.received"), at: transfer.receivedAt },
  ];

  return (
    <>
      <PageHeader
        title={transfer.number}
        description={t("route", { from: transfer.fromBranchCode, to: transfer.toBranchCode })}
        crumbs={crumbs}
        actions={
          <>
            <TransferStatusBadge status={transfer.status} />
            {allowed.has("approve") ? (
              <Button onClick={() => void act("approve")} loading={run.isPending}>
                {t("actions.approve")}
              </Button>
            ) : null}
            {allowed.has("ship") ? <Button onClick={() => setConfirming("ship")}>{t("actions.ship")}</Button> : null}
            {allowed.has("receive") ? (
              <Button onClick={() => void act("receive")} loading={run.isPending}>
                {t("actions.receive")}
              </Button>
            ) : null}
            {allowed.has("reject") ? (
              <Button variant="secondary" onClick={() => setConfirming("reject")}>
                {t("actions.reject")}
              </Button>
            ) : null}
            {allowed.has("cancel") ? (
              <Button variant="secondary" onClick={() => setConfirming("cancel")}>
                {t("actions.cancel")}
              </Button>
            ) : null}
          </>
        }
      />
      <InventoryTabs />

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>{t("lines")}</CardTitle>
            </CardHeader>
            <CardContent>
              <ul className="divide-y divide-line" aria-label={t("lines")}>
                {transfer.lines.map((line) => (
                  <li key={line.variantId} className="flex flex-wrap items-center justify-between gap-3 py-3">
                    <div className="min-w-0">
                      <Link href={`/admin/products/${line.productId}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
                        <span className="[overflow-wrap:anywhere]">{line.productName}</span>
                      </Link>
                      <VariantText sku={line.sku} colorName={line.colorName} size={line.size} />
                    </div>
                    <p className="text-strong">
                      <span className="text-sm text-muted">{t("quantity")}: </span>
                      <span className="text-lg font-semibold tabular-nums">{toNumber(line.quantity)}</span>
                    </p>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>
        </div>

        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>{t("summary")}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <p className="flex items-center gap-2 text-lg font-semibold text-strong">
                {transfer.fromBranchCode}
                <ArrowRight className="size-5 text-muted rtl:rotate-180" aria-hidden />
                <span className="sr-only">{t("to")}</span>
                {transfer.toBranchCode}
              </p>
              {transfer.note ? (
                <div>
                  <p className="text-sm font-semibold text-strong">{t("note")}</p>
                  <p className="text-muted [overflow-wrap:anywhere]">{transfer.note}</p>
                </div>
              ) : null}
              {closedBad && transfer.cancelReason ? (
                <Alert variant="warning" title={transfer.status === "Rejected" ? t("rejectedBecause") : t("cancelledBecause")}>
                  {transfer.cancelReason}
                </Alert>
              ) : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("progress")}</CardTitle>
            </CardHeader>
            <CardContent>
              <ol className="space-y-3">
                {steps.map((step) => {
                  const done = !!step.at;
                  return (
                    <li key={step.id} className="flex items-start gap-3">
                      {done ? <CheckCircle2 className="mt-0.5 size-5 shrink-0 text-success" aria-hidden /> : <Circle className="mt-0.5 size-5 shrink-0 text-muted" aria-hidden />}
                      <div className="min-w-0">
                        <p className={done ? "font-semibold text-strong" : "text-muted"}>{step.label}</p>
                        <p className="text-sm text-muted">{done ? dateTime(step.at) : t("timeline.waiting")}</p>
                      </div>
                    </li>
                  );
                })}
                {closedBad ? (
                  <li className="flex items-start gap-3">
                    <XCircle className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden />
                    <div className="min-w-0">
                      <p className="font-semibold text-strong">{transfer.status === "Rejected" ? t("timeline.rejected") : t("timeline.cancelled")}</p>
                      <p className="text-sm text-muted">{dateTime(transfer.closedAt)}</p>
                    </div>
                  </li>
                ) : null}
              </ol>
            </CardContent>
          </Card>
        </div>
      </div>

      <ConfirmDialog
        open={confirming === "ship"}
        onOpenChange={(open) => !open && setConfirming(null)}
        title={t("confirm.ship.title")}
        description={t("confirm.ship.description", { from: transfer.fromBranchCode })}
        confirmLabel={t("confirm.ship.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={run.isPending}
        onConfirm={() => act("ship")}
      />
      <ConfirmDialog
        open={confirming === "reject" || confirming === "cancel"}
        onOpenChange={(open) => {
          if (!open) {
            setConfirming(null);
            setReason("");
          }
        }}
        title={confirming === "reject" ? t("confirm.reject.title") : t("confirm.cancel.title")}
        description={
          confirming === "reject"
            ? t("confirm.reject.description")
            : transfer.status === "InTransit"
              ? t("confirm.cancel.descriptionInTransit", { from: transfer.fromBranchCode })
              : t("confirm.cancel.description")
        }
        confirmLabel={confirming === "reject" ? t("confirm.reject.confirm") : t("confirm.cancel.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={run.isPending}
        onConfirm={() => act(confirming === "reject" ? "reject" : "cancel", true)}
      >
        <Field label={t("reasonLabel")} htmlFor="tr-reason" hint={t("reasonHint")}>
          <Textarea id="tr-reason" value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} className="min-h-20" />
        </Field>
      </ConfirmDialog>
    </>
  );
}
