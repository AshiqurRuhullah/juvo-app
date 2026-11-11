"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Ban, CircleCheck, PackageCheck, Printer, ShieldAlert, Truck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Checkbox, Field, Label, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useOrder, useOrderAction, useOrderErrorText, problemOf, type NoteKind, type OrderAction, type OrderStatus } from "./api";
import { AmountList, FraudBadge, OrderLines, OrderStatusBadge, PaymentStatusBadge, ProgressLine, RiskChips, Section, TrackBadges, TrackHistory, useMethodLabel, useSourceLabel } from "./common";
import { AddressBlock } from "./my-order-detail";
import { useOrderPermissions } from "./permissions";
import { actionsFrom } from "./status";

type Dialogs = "confirm" | "cancel" | "reassign" | "transition" | null;

/** One order for staff: the three tracks, the immutable history, the items as sold, the customer, risk flags, notes and the actions this person may take. */
export function OrderDetail({ id }: { id: string }) {
  const t = useTranslations("orders.detail");
  const tn = useTranslations("nav");
  const ts = useTranslations("orders.status");
  const tc = useTranslations("orders.common");
  const dateTime = useDateTime();
  const money = useMoney();
  const errorText = useOrderErrorText();
  const methodLabel = useMethodLabel();
  const sourceLabel = useSourceLabel();
  const { me } = useSession();
  const query = useOrder(id);
  const order = query.data;
  const perms = useOrderPermissions(order?.branchId);
  const act = useOrderAction(id);

  const [dialog, setDialog] = React.useState<Dialogs>(null);
  const [reason, setReason] = React.useState("");
  const [reasonError, setReasonError] = React.useState<string | null>(null);
  const [advance, setAdvance] = React.useState(false);
  const [advanceError, setAdvanceError] = React.useState(false);
  const [branchId, setBranchId] = React.useState("");
  const [step, setStep] = React.useState("");
  const [noteKind, setNoteKind] = React.useState<NoteKind>("Internal");
  const [noteText, setNoteText] = React.useState("");

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.orders"), href: "/admin/orders" }, { label: order?.number ?? "..." }];

  if (query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy />
      </>
    );
  }
  if (query.isError || !order) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFound") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/orders">{t("back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const close = () => {
    setDialog(null);
    setReason("");
    setReasonError(null);
    setAdvance(false);
    setAdvanceError(false);
  };
  const run = async (action: OrderAction, done: string): Promise<boolean> => {
    try {
      await act.mutateAsync(action);
      toast.success(t(`done.${done}`));
      return true;
    } catch (failure) {
      const code = problemOf(failure)?.code;
      if (code === "ORDER.ADVANCE_REQUIRED") {
        setDialog("confirm");
        setAdvanceError(true);
      } else toast.error(errorText(failure));
      return false;
    }
  };

  const allowed = actionsFrom(order.nextOrderStatuses);
  const advanceDue = toNumber(order.money.advanceRequired) > 0 && order.paymentStatus !== "Paid";
  const branches = me.branches.filter((b) => b.isActive && b.id !== order.branchId);
  const canReassign = perms.reassign && order.status !== "Packed" && ["Placed", "Confirmed"].includes(order.status);
  const noActions = !(allowed.confirm && perms.confirm) && !(allowed.pack && perms.pack) && !(allowed.cancel && perms.cancel) && !(allowed.steps.length > 0 && perms.edit) && !canReassign;

  const onConfirmClick = () => (advanceDue ? setDialog("confirm") : void run({ type: "confirm" }, "confirm"));

  return (
    <>
      <PageHeader
        title={order.number}
        description={t("placedOn", { date: dateTime(order.placedAt, "long"), source: sourceLabel(order.source) })}
        crumbs={crumbs}
        actions={
          <>
            <OrderStatusBadge status={order.status} />
            <PaymentStatusBadge status={order.paymentStatus} />
            <FraudBadge state={order.fraudState} />
            {allowed.confirm && perms.confirm ? (
              <Button onClick={onConfirmClick} loading={act.isPending && dialog === null} disabled={order.isHeld} data-testid="action-confirm">
                <CircleCheck aria-hidden /> {t("actions.confirm")}
              </Button>
            ) : null}
            {allowed.pack && perms.pack ? (
              <Button onClick={() => void run({ type: "pack" }, "pack")} loading={act.isPending && dialog === null} data-testid="action-pack">
                <PackageCheck aria-hidden /> {t("actions.pack")}
              </Button>
            ) : null}
            {allowed.steps.length > 0 && perms.edit ? (
              <Button
                variant="secondary"
                onClick={() => {
                  setStep(allowed.steps[0]!);
                  setDialog("transition");
                }}
                data-testid="action-transition"
              >
                <Truck aria-hidden /> {t("actions.delivery")}
              </Button>
            ) : null}
            {canReassign && branches.length > 0 ? (
              <Button
                variant="secondary"
                onClick={() => {
                  setBranchId(branches[0]!.id);
                  setDialog("reassign");
                }}
                data-testid="action-reassign"
              >
                {t("actions.reassign")}
              </Button>
            ) : null}
            {perms.print ? (
              <Button asChild variant="secondary">
                <Link href={`/admin/orders/${order.id}/packing-slip`} data-testid="action-print">
                  <Printer aria-hidden /> {t("actions.print")}
                </Link>
              </Button>
            ) : null}
            {allowed.cancel && perms.cancel ? (
              <Button variant="secondary" onClick={() => setDialog("cancel")} data-testid="action-cancel">
                <Ban aria-hidden /> {t("actions.cancel")}
              </Button>
            ) : null}
          </>
        }
      />

      <div className="space-y-5">
        {order.isHeld ? (
          <Alert variant="danger" title={t("held.title")} data-testid="held-banner">
            {t("held.text")}{" "}
            {perms.fraudView ? (
              <Link href="/admin/fraud" className="inline-flex min-h-11 items-center font-semibold text-strong underline underline-offset-4">
                {t("held.open")}
              </Link>
            ) : null}
          </Alert>
        ) : null}
        {noActions && order.nextOrderStatuses.length > 0 ? <p className="text-sm text-muted">{t("noActions")}</p> : null}
        {order.cancelReason ? (
          <Alert variant="warning" title={t("cancelledBecause")}>
            {order.cancelReason}
          </Alert>
        ) : null}

        <Section title={t("tracks")} id="od-tracks">
          <TrackBadges status={order.status} paymentStatus={order.paymentStatus} deliveryStatus={order.deliveryStatus} />
          {order.paymentHoldExpiresAt && order.paymentStatus === "Pending" ? <p className="mt-3 text-sm text-muted">{t("holdUntil", { time: dateTime(order.paymentHoldExpiresAt) })}</p> : null}
        </Section>

        <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,24rem)]">
          <div className="min-w-0 space-y-5">
            <Section title={t("items")} id="od-items">
              <OrderLines lines={order.items} label={t("items")} />
              <p className="mt-3 text-sm text-muted">{t("snapshot")}</p>
            </Section>

            <div className="grid gap-5 xl:grid-cols-2">
              <Section title={t("progress")} id="od-progress">
                <ProgressLine status={order.status} timeline={order.timeline} />
              </Section>
              <Section title={t("history")} id="od-history" className="space-y-4">
                {(["Order", "Payment", "Delivery"] as const).map((track) => (
                  <div key={track} className="space-y-2">
                    <h3 className="text-sm font-semibold text-strong">{tc(`track${track}`)}</h3>
                    <TrackHistory timeline={order.timeline} track={track} showActor />
                  </div>
                ))}
              </Section>
            </div>

            <Section title={t("notes")} id="od-notes">
              {order.notes.length === 0 ? <p className="text-sm text-muted">{t("noNotes")}</p> : null}
              <ul className="space-y-3" data-testid="notes">
                {order.notes.map((n) => (
                  <li key={n.id} className="rounded-control bg-sunken p-3">
                    <p className="flex flex-wrap items-center gap-2 text-sm text-muted">
                      <Badge variant={n.kind === "Customer" ? "info" : "neutral"}>{t(`noteKind.${n.kind}`)}</Badge>
                      {n.authorName ? `${n.authorName} · ` : ""}
                      {dateTime(n.at)}
                    </p>
                    <p className="mt-1 text-strong [overflow-wrap:anywhere]">{n.text}</p>
                  </li>
                ))}
              </ul>
              {perms.note ? (
                <form
                  className="mt-4 space-y-3 border-t border-line pt-4"
                  onSubmit={async (event) => {
                    event.preventDefault();
                    if (!noteText.trim()) return;
                    if (await run({ type: "note", kind: noteKind, text: noteText.trim() }, "note")) setNoteText("");
                  }}
                >
                  <RadioGroup value={noteKind} onValueChange={(v) => setNoteKind(v === "Customer" ? "Customer" : "Internal")} className="grid gap-2 sm:grid-cols-2" aria-label={t("noteKindLabel")}>
                    {(["Internal", "Customer"] as const).map((kind) => (
                      <div key={kind} className="flex min-h-12 items-center gap-3 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                        <RadioItem id={`note-kind-${kind}`} value={kind} />
                        <Label htmlFor={`note-kind-${kind}`} className="flex min-h-11 flex-1 cursor-pointer flex-col justify-center">
                          <span>{t(`noteKind.${kind}`)}</span>
                          <span className="text-xs font-normal text-muted">{t(`noteHelp.${kind}`)}</span>
                        </Label>
                      </div>
                    ))}
                  </RadioGroup>
                  <Field label={t("noteText")} htmlFor="note-text">
                    <Textarea id="note-text" rows={3} maxLength={1000} value={noteText} onChange={(e) => setNoteText(e.target.value)} />
                  </Field>
                  <Button type="submit" variant="secondary" loading={act.isPending && dialog === null} disabled={!noteText.trim()} data-testid="add-note">
                    {t("addNote")}
                  </Button>
                </form>
              ) : null}
            </Section>
          </div>

          <div className="min-w-0 space-y-5">
            <Section title={t("amounts")} id="od-amounts">
              <AmountList
                rows={[
                  { key: "subtotal", label: t("subtotal"), value: order.money.subtotal, always: true },
                  { key: "coupon", label: t("coupon", { code: order.money.couponCode ?? "" }), value: order.money.couponDiscount, negative: true },
                  { key: "discount", label: t("discount"), value: order.money.manualDiscount, negative: true },
                  { key: "delivery", label: t("delivery"), value: order.money.deliveryCharge, always: true },
                  { key: "total", label: t("total"), value: order.money.grandTotal, strong: true },
                  { key: "advance", label: t("advance"), value: order.money.advanceRequired },
                ]}
              />
              <p className="mt-3 text-sm text-muted">{t("method", { method: methodLabel(order.paymentMethod) })}</p>
              <p className="text-sm text-muted">{t("weight", { grams: toNumber(order.weightGrams) })}</p>
            </Section>

            <Section title={t("customer")} id="od-customer">
              <dl className="space-y-1.5 text-sm">
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("name")}</dt>
                  <dd className="text-end font-medium text-strong [overflow-wrap:anywhere]">{order.customerName ?? "-"}</dd>
                </div>
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("mobile")}</dt>
                  <dd className="text-end font-medium text-strong" data-testid="customer-mobile">
                    {order.customerMobile}
                  </dd>
                </div>
                {order.customerEmail ? (
                  <div className="flex justify-between gap-3">
                    <dt className="text-muted">{t("email")}</dt>
                    <dd className="text-end text-strong [overflow-wrap:anywhere]">{order.customerEmail}</dd>
                  </div>
                ) : null}
              </dl>
              <h3 className="mb-1 mt-4 text-sm font-semibold text-strong">{t("address")}</h3>
              <AddressBlock shipping={order.shipping} />
              <h3 className="mb-1 mt-4 text-sm font-semibold text-strong">{t("history2")}</h3>
              <dl className="grid grid-cols-2 gap-x-3 gap-y-1 text-sm" data-testid="customer-stats">
                {(
                  [
                    ["totalOrders", order.customerStats.totalOrders],
                    ["delivered", order.customerStats.delivered],
                    ["cancelled", order.customerStats.cancelled],
                    ["returnedOrFailed", order.customerStats.returnedOrFailed],
                    ["open", order.customerStats.open],
                    ["score", order.customerStats.score],
                  ] as const
                ).map(([key, value]) => (
                  <React.Fragment key={key}>
                    <dt className="text-muted">{t(`stats.${key}`)}</dt>
                    <dd className="text-end font-medium tabular-nums text-strong">{toNumber(value)}</dd>
                  </React.Fragment>
                ))}
                <dt className="text-muted">{t("stats.spent")}</dt>
                <dd className="text-end font-medium text-strong">{money(order.customerStats.totalSpent)}</dd>
              </dl>
            </Section>

            {order.flags.length > 0 || order.fraudLog.length > 0 || order.fraudState !== "None" ? (
              <Section title={t("risk")} id="od-risk" className="space-y-3">
                <p className="flex flex-wrap items-center gap-2 text-sm">
                  <ShieldAlert className="size-5 text-danger" aria-hidden />
                  <span className="text-muted">{t("riskScore")}</span> <strong className="text-strong">{toNumber(order.riskScore)}</strong>
                  <FraudBadge state={order.fraudState} />
                </p>
                <RiskChips flags={order.flags} />
                {order.fraudLog.length > 0 ? (
                  <ol className="space-y-2 border-t border-line pt-3 text-sm" aria-label={t("fraudLog")}>
                    {order.fraudLog.map((entry, index) => (
                      <li key={`${entry.at}-${index}`}>
                        <p className="font-medium text-strong">
                          {ts(`fraudAction.${entry.action}`)} <span className="font-normal text-muted">({ts(`fraud.${entry.fromState}`)} → {ts(`fraud.${entry.toState}`)})</span>
                        </p>
                        <p className="text-muted">
                          {dateTime(entry.at)}
                          {entry.actorName ? ` · ${entry.actorName}` : ""}
                        </p>
                        {entry.note ? <p className="text-muted [overflow-wrap:anywhere]">{entry.note}</p> : null}
                      </li>
                    ))}
                  </ol>
                ) : null}
              </Section>
            ) : null}

            <Section title={t("fulfilment")} id="od-fulfilment">
              <dl className="space-y-1.5 text-sm">
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("branch")}</dt>
                  <dd className="text-end font-medium text-strong" data-testid="order-branch">
                    {order.branchCode} · {order.branchName}
                  </dd>
                </div>
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("routing")}</dt>
                  <dd className="text-end text-strong">{order.routingRule}</dd>
                </div>
                {order.routingPending ? (
                  <div>
                    <Badge variant="warning">{t("routingPending")}</Badge>
                  </div>
                ) : null}
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("stock")}</dt>
                  <dd className="text-end text-strong">{order.stockDeducted ? t("stockDeducted") : t("stockReserved")}</dd>
                </div>
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("otp")}</dt>
                  <dd className="text-end text-strong">{order.otpVerified ? t("yes") : t("no")}</dd>
                </div>
              </dl>
            </Section>
          </div>
        </div>
      </div>

      <ConfirmDialog
        open={dialog === "confirm"}
        onOpenChange={(open) => (open ? undefined : close())}
        title={t("confirmDialog.title", { number: order.number })}
        description={advanceDue ? t("confirmDialog.advanceText", { amount: money(order.money.advanceRequired) }) : t("confirmDialog.text")}
        confirmLabel={t("actions.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={act.isPending}
        onConfirm={async () => {
          if (advanceDue && !advance) return setAdvanceError(true);
          if (await run({ type: "confirm", advanceReceived: advance }, "confirm")) close();
        }}
      >
        {advanceDue ? (
          <div className="space-y-1">
            <div className="flex min-h-11 items-start gap-3">
              <Checkbox id="advance-received" checked={advance} onCheckedChange={(v) => (setAdvance(v === true), setAdvanceError(false))} className="mt-0.5" aria-invalid={advanceError} />
              <Label htmlFor="advance-received" className="flex-1 cursor-pointer py-2.5 font-normal">
                {t("confirmDialog.received", { amount: money(order.money.advanceRequired) })}
              </Label>
            </div>
            {advanceError ? (
              <p role="alert" className="text-sm font-medium text-danger">
                {t("confirmDialog.needTick")}
              </p>
            ) : null}
          </div>
        ) : null}
      </ConfirmDialog>

      <ConfirmDialog
        open={dialog === "cancel"}
        onOpenChange={(open) => (open ? undefined : close())}
        title={t("cancelDialog.title", { number: order.number })}
        description={t("cancelDialog.text")}
        confirmLabel={t("actions.cancel")}
        cancelLabel={t("cancelDialog.keep")}
        closeLabel={tc("close")}
        destructive
        loading={act.isPending}
        onConfirm={async () => {
          if (!reason.trim()) return setReasonError(t("reasonRequired"));
          if (await run({ type: "cancel", reason: reason.trim() }, "cancel")) close();
        }}
      >
        <Field label={t("reason")} htmlFor="cancel-reason" required error={reasonError ?? undefined}>
          <Textarea id="cancel-reason" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} aria-invalid={!!reasonError} />
        </Field>
      </ConfirmDialog>

      <ConfirmDialog
        open={dialog === "reassign"}
        onOpenChange={(open) => (open ? undefined : close())}
        title={t("reassignDialog.title")}
        description={t("reassignDialog.text")}
        confirmLabel={t("actions.reassign")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={act.isPending}
        onConfirm={async () => {
          if (branchId && (await run({ type: "reassign", branchId, reason: reason.trim() || null }, "reassign"))) close();
        }}
      >
        <div className="space-y-3">
          <Field label={t("reassignDialog.branch")} htmlFor="reassign-branch">
            <Select id="reassign-branch" value={branchId} onValueChange={setBranchId} options={branches.map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` }))} placeholder={tc("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
          <Field label={t("reason")} htmlFor="reassign-reason" hint={t("optional")}>
            <Textarea id="reassign-reason" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
        </div>
      </ConfirmDialog>

      <ConfirmDialog
        open={dialog === "transition"}
        onOpenChange={(open) => (open ? undefined : close())}
        title={t("transitionDialog.title")}
        description={t("transitionDialog.text")}
        confirmLabel={t("transitionDialog.apply")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={act.isPending}
        onConfirm={async () => {
          if (step && (await run({ type: "transition", to: step as OrderStatus, reason: reason.trim() || null }, "transition"))) close();
        }}
      >
        <div className="space-y-3">
          <Field label={t("transitionDialog.next")} htmlFor="transition-to">
            <Select id="transition-to" value={step} onValueChange={setStep} options={allowed.steps.map((s) => ({ value: s, label: ts(`order.${s}`) }))} placeholder={tc("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} searchable={false} />
          </Field>
          <Field label={t("reason")} htmlFor="transition-reason" hint={t("optional")}>
            <Textarea id="transition-reason" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
        </div>
      </ConfirmDialog>
    </>
  );
}

