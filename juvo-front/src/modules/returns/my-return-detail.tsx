"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, Truck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Textarea } from "@/components/ui/form";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { mediaUrl } from "@/lib/media";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCancelMyReturn, useMyReturn } from "./api";
import { RefundBox, ReturnTimeline, ReturnStatusBadge, Thumb, VariantLine, useDay, useReasonLabel } from "./common";

/** One of my returns: where it stands, what was asked, the pickup, the refund and the history. */
export function MyReturnDetail({ id }: { id: string }) {
  const t = useTranslations("returns.mineDetail");
  const tm = useTranslations("returns.mine");
  const tc = useTranslations("returns.common");
  const tn = useTranslations("nav");
  const money = useMoney();
  const dateTime = useDateTime();
  const day = useDay();
  const reasonLabel = useReasonLabel();
  const problemMessage = useProblemMessage();
  const query = useMyReturn(id);
  const cancel = useCancelMyReturn(id);
  const [confirming, setConfirming] = React.useState(false);
  const [reason, setReason] = React.useState("");

  const crumbs = [
    { label: tn("items.accountHome"), href: "/account" },
    { label: tn("items.returns"), href: "/account/returns" },
    { label: query.data?.number ?? "..." },
  ];

  if (query.isPending) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy="true" />
      </>
    );
  }
  if (query.isError || !query.data) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFound") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/account/returns">
                <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
              </Link>
            </Button>
          }
        />
      </>
    );
  }

  const r = query.data;
  const doCancel = async () => {
    try {
      await cancel.mutateAsync(reason);
      toast.success(t("cancelled"));
      setConfirming(false);
      setReason("");
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      setConfirming(false);
    }
  };

  return (
    <>
      <PageHeader
        title={t("title", { number: r.number })}
        description={tm("order", { number: r.orderNumber })}
        crumbs={crumbs}
        actions={
          <>
            <ReturnStatusBadge status={r.status} />
            <Badge variant="outline">{tm(`kind.${r.kind}`)}</Badge>
            {r.canCancel ? (
              <Button variant="secondary" onClick={() => setConfirming(true)}>
                {t("cancel")}
              </Button>
            ) : null}
          </>
        }
      />

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
        <div className="space-y-6">
          <Alert variant={r.status === "Rejected" || r.status === "RefundRejected" ? "danger" : "info"} title={t("whereTitle")} data-testid="return-next-step">
            {t(`nextStep.${r.status}`)}
            {r.rejectReason ? (
              <span className="mt-1 block font-medium" data-testid="reject-reason">
                {t("rejectReason", { reason: r.rejectReason })}
              </span>
            ) : null}
          </Alert>

          <Card>
            <CardHeader>
              <CardTitle>{t("items")}</CardTitle>
            </CardHeader>
            <CardContent>
              <ul className="divide-y divide-line" aria-label={t("items")}>
                {r.items.map((item) => (
                  <li key={item.id} className="flex items-start gap-3 py-3 first:pt-0 last:pb-0" data-testid="my-return-item">
                    <Thumb src={item.imageUrl} size={64} />
                    <div className="min-w-0 flex-1">
                      <p className="font-semibold text-strong [overflow-wrap:anywhere]">{item.name}</p>
                      <VariantLine sku={item.sku} colorName={item.colorName} size={item.size} />
                      <p className="mt-1 text-sm text-muted">
                        {t("quantity", { count: toNumber(item.quantity) })}
                        {item.exchangeSku && r.kind === "Exchange" ? ` · ${t("replacementSku", { sku: item.exchangeSku })}` : ""}
                      </p>
                    </div>
                  </li>
                ))}
              </ul>
              <dl className="mt-4 grid gap-3 border-t border-line pt-4 sm:grid-cols-2">
                <div>
                  <dt className="text-sm text-muted">{t("reason")}</dt>
                  <dd className="font-medium text-strong">{reasonLabel(r.reasonCode)}</dd>
                </div>
                <div>
                  <dt className="text-sm text-muted">{t("requestedAt")}</dt>
                  <dd className="font-medium text-strong">{dateTime(r.requestedAt)}</dd>
                </div>
                {r.reasonText ? (
                  <div className="sm:col-span-2">
                    <dt className="text-sm text-muted">{t("details")}</dt>
                    <dd className="text-strong [overflow-wrap:anywhere]">{r.reasonText}</dd>
                  </div>
                ) : null}
              </dl>
              {r.photos.length > 0 ? (
                <ul className="mt-4 flex flex-wrap gap-2" aria-label={t("photos")}>
                  {r.photos.map((photo, index) => (
                    <li key={photo.path}>
                      <a href={mediaUrl(photo.url)} target="_blank" rel="noreferrer" className="block rounded-control focus-visible:outline-2" aria-label={t("openPhoto", { number: index + 1 })}>
                        <Thumb src={photo.thumbUrl} size={72} />
                      </a>
                    </li>
                  ))}
                </ul>
              ) : null}
            </CardContent>
          </Card>

          {r.pickup && (r.pickup.date || r.pickup.courier) ? (
            <Card>
              <CardHeader>
                <CardTitle className="flex items-center gap-2">
                  <Truck className="size-5" aria-hidden /> {t("pickup")}
                </CardTitle>
              </CardHeader>
              <CardContent>
                <dl className="grid gap-3 sm:grid-cols-2" data-testid="pickup-info">
                  <div>
                    <dt className="text-sm text-muted">{t("pickupDate")}</dt>
                    <dd className="font-medium text-strong">{day(r.pickup.date)}</dd>
                  </div>
                  {r.pickup.courier ? (
                    <div>
                      <dt className="text-sm text-muted">{t("pickupCourier")}</dt>
                      <dd className="font-medium text-strong [overflow-wrap:anywhere]">{r.pickup.courier}</dd>
                    </div>
                  ) : null}
                  {r.pickup.note ? (
                    <div className="sm:col-span-2">
                      <dt className="text-sm text-muted">{t("pickupNote")}</dt>
                      <dd className="text-strong [overflow-wrap:anywhere]">{r.pickup.note}</dd>
                    </div>
                  ) : null}
                </dl>
              </CardContent>
            </Card>
          ) : null}
        </div>

        <div className="space-y-6">
          {r.kind === "Return" && r.status !== "Rejected" && r.status !== "Cancelled" && r.status !== "RefundRejected" ? (
            <Card>
              <CardHeader>
                <CardTitle>{t("refund")}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <RefundBox refund={r.refund} />
                {r.refundPaid ? (
                  <Alert variant="success" title={t("refundPaidTitle")} data-testid="refund-paid">
                    {t("refundPaid", { amount: money(r.refundPaid.amount), method: tc(`method.${r.refundPaid.method}`), date: dateTime(r.refundPaid.at) })}
                    <span className="mt-1 block text-sm">{t("refundReference", { reference: r.refundPaid.reference })}</span>
                  </Alert>
                ) : null}
              </CardContent>
            </Card>
          ) : null}

          {r.kind === "Exchange" && r.replacementOrderNumber ? (
            <Card>
              <CardHeader>
                <CardTitle>{t("replacement")}</CardTitle>
              </CardHeader>
              <CardContent>
                <p className="text-strong" data-testid="replacement-order">
                  {t("replacementOrder", { number: r.replacementOrderNumber })}
                </p>
              </CardContent>
            </Card>
          ) : null}

          <Card>
            <CardHeader>
              <CardTitle>{t("history")}</CardTitle>
            </CardHeader>
            <CardContent>
              <ReturnTimeline entries={r.timeline} dateTime={(v) => dateTime(v)} />
            </CardContent>
          </Card>
        </div>
      </div>

      <ConfirmDialog
        open={confirming}
        onOpenChange={(open) => {
          setConfirming(open);
          if (!open) setReason("");
        }}
        title={t("confirmCancel.title")}
        description={t("confirmCancel.description", { number: r.number })}
        confirmLabel={t("confirmCancel.confirm")}
        cancelLabel={t("confirmCancel.keep")}
        closeLabel={tc("close")}
        destructive
        loading={cancel.isPending}
        onConfirm={doCancel}
      >
        <Field label={t("cancelReason")} htmlFor="my-cancel-reason" hint={t("cancelReasonHint")}>
          <Textarea id="my-cancel-reason" value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} className="min-h-20" />
        </Field>
      </ConfirmDialog>
    </>
  );
}
