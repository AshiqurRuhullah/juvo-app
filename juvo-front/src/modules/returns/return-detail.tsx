"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, CircleCheck, CircleX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Textarea } from "@/components/ui/form";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { canInBranch } from "@/lib/auth/permissions";
import { mediaUrl } from "@/lib/media";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useAddReturnNote, useReturn, type ReturnDetail } from "./api";
import { RefundBox, ReturnStatusBadge, ReturnTimeline, Thumb, VariantLine, useDay, useReasonLabel } from "./common";
import { ReturnActions } from "./return-actions";

/** One return for staff: who and what, the photos, the refund, the full history and the next steps this person may take. */
export function ReturnDetailView({ id }: { id: string }) {
  const t = useTranslations("returns.detail");
  const tc = useTranslations("returns.common");
  const tk = useTranslations("returns.kind");
  const tf = useTranslations("returns.fault");
  const tn = useTranslations("nav");
  const money = useMoney();
  const dateTime = useDateTime();
  const day = useDay();
  const reasonLabel = useReasonLabel();
  const query = useReturn(id);

  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.returns"), href: "/admin/returns" },
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
              <Link href="/admin/returns">
                <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
              </Link>
            </Button>
          }
        />
      </>
    );
  }

  const r = query.data;
  const inspected = r.inspectedAt !== null;

  return (
    <>
      <PageHeader
        title={r.number}
        description={t("subtitle", { order: r.orderNumber, branch: r.branchCode })}
        crumbs={crumbs}
        actions={
          <>
            <ReturnStatusBadge status={r.status} />
            <Badge variant="outline">{tk(r.kind)}</Badge>
          </>
        }
      />

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,24rem)]">
        <div className="space-y-6">
          {r.nextStatuses.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>{t("nextStep")}</CardTitle>
              </CardHeader>
              <CardContent>
                <ReturnActions detail={r} />
                <NoActionsHint detail={r} />
              </CardContent>
            </Card>
          ) : null}

          {r.status === "Rejected" || r.status === "RefundRejected" ? (
            <Alert variant="danger" title={r.status === "Rejected" ? t("rejectedTitle") : t("refundRejectedTitle")} data-testid="reject-reason">
              {r.rejectReason ?? "-"}
            </Alert>
          ) : null}

          <Card>
            <CardHeader>
              <CardTitle>{t("items")}</CardTitle>
            </CardHeader>
            <CardContent>
              <ul className="divide-y divide-line" aria-label={t("items")}>
                {r.items.map((item) => (
                  <li key={item.id} className="flex items-start gap-3 py-4 first:pt-0 last:pb-0" data-testid="return-item">
                    <Thumb src={item.imageUrl} size={64} />
                    <div className="min-w-0 flex-1 space-y-1">
                      <Link href={`/admin/products/${item.productId}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline [overflow-wrap:anywhere]">
                        {item.name}
                      </Link>
                      <VariantLine sku={item.sku} colorName={item.colorName} size={item.size} className="-mt-2" />
                      <p className="text-sm text-muted">{t("lineFacts", { quantity: toNumber(item.quantity), price: money(item.unitPrice), refund: money(inspected ? item.refundAmount : item.lineRefund) })}</p>
                      {r.kind === "Exchange" && item.exchangeSku ? <p className="text-sm text-muted">{t("replacementSku", { sku: item.exchangeSku })}</p> : null}
                      {inspected ? (
                        <p className="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm" data-testid="inspection-result">
                          <span className="text-strong">{t("condition", { condition: item.condition ? tc(`condition.${item.condition}`) : "-" })}</span>
                          <span className="inline-flex items-center gap-1 text-strong">
                            {item.restocked ? <CircleCheck className="size-4 text-success" aria-hidden /> : <CircleX className="size-4 text-muted" aria-hidden />}
                            {item.restocked ? t("restocked") : t("notRestocked")}
                          </span>
                          {!item.refundAccepted ? (
                            <span className="inline-flex items-center gap-1 font-medium text-danger-ink">
                              <CircleX className="size-4" aria-hidden /> {t("notAccepted")}
                            </span>
                          ) : null}
                        </p>
                      ) : null}
                    </div>
                  </li>
                ))}
              </ul>
            </CardContent>
          </Card>

          {r.photos.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>{t("photos")}</CardTitle>
              </CardHeader>
              <CardContent>
                <ul className="flex flex-wrap gap-3" aria-label={t("photos")} data-testid="return-photos">
                  {r.photos.map((photo, index) => (
                    <li key={photo.path}>
                      <a href={mediaUrl(photo.url)} target="_blank" rel="noreferrer" aria-label={t("openPhoto", { number: index + 1 })} className="block rounded-control">
                        <Thumb src={photo.thumbUrl} size={96} />
                      </a>
                    </li>
                  ))}
                </ul>
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

        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>{t("customer")}</CardTitle>
            </CardHeader>
            <CardContent>
              <dl className="space-y-3">
                <div>
                  <dt className="text-sm text-muted">{t("name")}</dt>
                  <dd className="font-medium text-strong [overflow-wrap:anywhere]">{r.customerName ?? "-"}</dd>
                </div>
                <div>
                  <dt className="text-sm text-muted">{t("mobile")}</dt>
                  <dd className="font-medium text-strong">{r.customerMobile}</dd>
                </div>
                <div>
                  <dt className="text-sm text-muted">{t("order")}</dt>
                  <dd>
                    <Link href={`/admin/orders/${r.orderId}`} className="inline-flex min-h-11 items-center rounded-control font-mono font-semibold text-strong hover:text-primary hover:underline" data-testid="order-link">
                      {r.orderNumber}
                    </Link>
                    <span className="block text-sm text-muted">
                      {t("orderFacts", { status: r.order.status, payment: r.order.paymentStatus, total: money(r.order.grandTotal) })}
                    </span>
                  </dd>
                </div>
                <div>
                  <dt className="text-sm text-muted">{t("reason")}</dt>
                  <dd className="font-medium text-strong">{reasonLabel(r.reasonCode)}</dd>
                  {r.reasonText ? <dd className="mt-1 text-strong [overflow-wrap:anywhere]">{r.reasonText}</dd> : null}
                </div>
                <div>
                  <dt className="text-sm text-muted">{t("fault")}</dt>
                  <dd className="font-medium text-strong">{tf(r.fault)}</dd>
                </div>
                <div>
                  <dt className="text-sm text-muted">{t("requestedAt")}</dt>
                  <dd className="font-medium text-strong">{dateTime(r.requestedAt)}</dd>
                </div>
              </dl>
            </CardContent>
          </Card>

          {r.kind === "Return" ? (
            <Card>
              <CardHeader>
                <CardTitle>{t("refund")}</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3">
                <RefundBox refund={r.refund} />
                {r.refundPaid ? (
                  <Alert variant="success" title={t("refundPaidTitle")} data-testid="refund-paid">
                    {t("refundPaid", { amount: money(r.refundPaid.amount), method: tc(`method.${r.refundPaid.method}`) })}
                    <span className="mt-1 block text-sm">{t("refundReference", { reference: r.refundPaid.reference })}</span>
                    <span className="block text-sm">{t("refundBy", { name: r.refundPaid.actorName ?? "-", date: dateTime(r.refundPaid.at) })}</span>
                  </Alert>
                ) : null}
              </CardContent>
            </Card>
          ) : r.replacementOrderNumber ? (
            <Card>
              <CardHeader>
                <CardTitle>{t("replacement")}</CardTitle>
              </CardHeader>
              <CardContent>
                <Link href={`/admin/orders/${r.replacementOrderId}`} className="inline-flex min-h-11 items-center rounded-control font-mono font-semibold text-strong hover:text-primary hover:underline" data-testid="replacement-order">
                  {r.replacementOrderNumber}
                </Link>
              </CardContent>
            </Card>
          ) : null}

          {r.pickup && (r.pickup.date || r.pickup.courier || r.pickup.note) ? (
            <Card>
              <CardHeader>
                <CardTitle>{t("pickup")}</CardTitle>
              </CardHeader>
              <CardContent>
                <dl className="space-y-3" data-testid="pickup-info">
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
                    <div>
                      <dt className="text-sm text-muted">{t("pickupNote")}</dt>
                      <dd className="text-strong [overflow-wrap:anywhere]">{r.pickup.note}</dd>
                    </div>
                  ) : null}
                </dl>
              </CardContent>
            </Card>
          ) : null}

          <NotesCard detail={r} />
        </div>
      </div>
    </>
  );
}

/** When the return is waiting for a step this person has no permission for, say so instead of showing an empty box. */
function NoActionsHint({ detail }: { detail: ReturnDetail }) {
  const t = useTranslations("returns.detail");
  const { me } = useSession();
  const holds = (p: string) => canInBranch(me, p, detail.branchId);
  const any = ["returns.return.approve", "returns.return.reject", "returns.return.manage", "returns.return.receive", "returns.return.inspect", "returns.return.refund", "returns.return.exchange"].some(holds);
  return any ? null : <p className="text-muted">{t("noPermission")}</p>;
}

function NotesCard({ detail }: { detail: ReturnDetail }) {
  const t = useTranslations("returns.notes");
  const dateTime = useDateTime();
  const problemMessage = useProblemMessage();
  const { me } = useSession();
  const add = useAddReturnNote(detail.id);
  const [text, setText] = React.useState("");
  const canAdd = canInBranch(me, "returns.return.manage", detail.branchId);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (text.trim() === "") return;
    try {
      await add.mutateAsync(text);
      setText("");
      toast.success(t("added"));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <p className="text-sm text-muted">{t("internal")}</p>
        {detail.notes.length === 0 ? <p className="text-muted">{t("none")}</p> : null}
        <ul className="space-y-3" aria-label={t("title")}>
          {detail.notes.map((note) => (
            <li key={note.id} className="rounded-control bg-sunken p-3" data-testid="return-note">
              <p className="text-strong [overflow-wrap:anywhere]">{note.text}</p>
              <p className="mt-1 text-sm text-muted">
                {note.authorName ?? "-"} · {dateTime(note.at)}
              </p>
            </li>
          ))}
        </ul>
        {canAdd ? (
          <form onSubmit={submit} className="space-y-3" noValidate>
            <Field label={t("label")} htmlFor="return-note">
              <Textarea id="return-note" value={text} maxLength={500} onChange={(e) => setText(e.target.value)} className="min-h-20" />
            </Field>
            <Button type="submit" variant="secondary" loading={add.isPending} disabled={text.trim() === ""}>
              {t("add")}
            </Button>
          </form>
        ) : null}
      </CardContent>
    </Card>
  );
}
