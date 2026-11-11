"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { MapPin, MessageCircle } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Textarea } from "@/components/ui/form";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useCancelMyOrder, useMyOrder, useOrderErrorText, type MyOrder } from "./api";
import { AmountList, OrderLines, ProgressLine, Section, TrackBadges, TrackHistory, useMethodLabel } from "./common";

/** The shipping address in a few readable lines. */
export function AddressBlock({ shipping }: { shipping: MyOrder["shipping"] }) {
  const parts = [shipping.addressLine, shipping.area, shipping.landmark].filter(Boolean).join(", ");
  const place = [shipping.upazila, shipping.district, shipping.division, shipping.city, shipping.stateProvince, shipping.postalCode, shipping.countryCode !== "BD" ? shipping.countryCode : null].filter(Boolean).join(", ");
  return (
    <address className="not-italic text-strong [overflow-wrap:anywhere]" data-testid="order-address">
      <span className="font-semibold">{shipping.recipientName}</span>, {shipping.phone}
      <br />
      {parts}
      <br />
      {place}
    </address>
  );
}

/** One order of the signed-in customer: tracks, progress, items, amounts, address, notes and a cancel button while it is allowed. */
export function MyOrderDetail({ id }: { id: string }) {
  const t = useTranslations("orders.mine.detail");
  const tn = useTranslations("nav");
  const tm = useTranslations("orders.mine");
  const dateTime = useDateTime();
  const errorText = useOrderErrorText();
  const methodLabel = useMethodLabel();
  const query = useMyOrder(id);
  const cancel = useCancelMyOrder(id);
  const [open, setOpen] = React.useState(false);
  const [reason, setReason] = React.useState("");
  const crumbs = [{ label: tn("items.accountHome"), href: "/account" }, { label: tm("title"), href: "/account/orders" }, { label: query.data?.number ?? "..." }];

  if (query.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy />
      </>
    );
  }
  if (query.isError || !query.data) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <Alert variant="danger">{notFound ? t("notFoundText") : errorText(query.error)}</Alert>
        <Link href="/account/orders" className={buttonVariants({ variant: "secondary", className: "mt-4" })}>
          {t("back")}
        </Link>
      </>
    );
  }

  const order = query.data;
  const doCancel = async () => {
    try {
      await cancel.mutateAsync(reason.trim() || null);
      toast.success(t("cancelled"));
      setOpen(false);
      setReason("");
    } catch (failure) {
      toast.error(errorText(failure));
      setOpen(false);
    }
  };

  return (
    <>
      <PageHeader
        title={order.number}
        description={t("placed", { date: dateTime(order.placedAt) })}
        crumbs={crumbs}
        actions={
          order.canCancel ? (
            <Button variant="secondary" onClick={() => setOpen(true)} data-testid="cancel-order">
              {t("cancel")}
            </Button>
          ) : null
        }
      />

      <div className="space-y-5">
        <Section title={t("status")} id="mo-status">
          <TrackBadges status={order.status} paymentStatus={order.paymentStatus} deliveryStatus={order.deliveryStatus} />
          {order.paymentDeadline && order.paymentStatus === "Pending" && order.paymentMethod !== "COD" ? (
            <Alert variant="warning" className="mt-4">
              {t("deadline", { time: dateTime(order.paymentDeadline) })}
            </Alert>
          ) : null}
        </Section>

        <div className="grid gap-5 lg:grid-cols-2">
          <Section title={t("progress")} id="mo-progress">
            <ProgressLine status={order.status} timeline={order.timeline} />
          </Section>
          <Section title={t("history")} id="mo-history" className="space-y-4">
            {(["Order", "Payment", "Delivery"] as const).map((track) => (
              <div key={track} className="space-y-2">
                <h3 className="text-sm font-semibold text-strong">{t(`track.${track}`)}</h3>
                <TrackHistory timeline={order.timeline} track={track} />
              </div>
            ))}
          </Section>
        </div>

        <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
          <Section title={t("items")} id="mo-items">
            <OrderLines lines={order.items} label={t("items")} />
          </Section>
          <div className="space-y-5">
            <Section title={t("amounts")} id="mo-amounts">
              <AmountList
                rows={[
                  { key: "subtotal", label: t("subtotal"), value: order.money.subtotal, always: true },
                  { key: "coupon", label: t("coupon"), value: order.money.couponDiscount, negative: true },
                  { key: "discount", label: t("discount"), value: order.money.manualDiscount, negative: true },
                  { key: "delivery", label: t("delivery"), value: order.money.deliveryCharge, always: true },
                  { key: "total", label: t("total"), value: order.money.grandTotal, strong: true },
                  { key: "advance", label: t("advance"), value: order.money.advanceRequired },
                ]}
              />
              <p className="mt-3 text-sm text-muted">{t("method", { method: methodLabel(order.paymentMethod) })}</p>
            </Section>
            <Section title={t("address")} id="mo-address">
              <p className="mb-2 flex items-center gap-2 text-sm text-muted">
                <MapPin className="size-4" aria-hidden /> {t("addressHelp")}
              </p>
              <AddressBlock shipping={order.shipping} />
            </Section>
          </div>
        </div>

        {order.notes.length > 0 ? (
          <Section title={t("notes")} id="mo-notes">
            <ul className="space-y-3">
              {order.notes.map((note) => (
                <li key={note.id} className="rounded-control bg-sunken p-3">
                  <p className="text-strong [overflow-wrap:anywhere]">{note.text}</p>
                  <p className="mt-1 text-sm text-muted">{dateTime(note.at)}</p>
                </li>
              ))}
            </ul>
          </Section>
        ) : null}

        <Alert variant="info" title={t("helpTitle")}>
          <span className="flex flex-wrap items-center gap-x-2">
            <MessageCircle className="size-4" aria-hidden /> {t("helpText", { number: order.number })}{" "}
            <a className="inline-flex min-h-11 items-center font-semibold text-strong underline underline-offset-4" href="mailto:juvobd@gmail.com">
              juvobd@gmail.com
            </a>
          </span>
        </Alert>
      </div>

      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title={t("cancelTitle")}
        description={t("cancelText", { number: order.number })}
        confirmLabel={t("cancelConfirm")}
        cancelLabel={t("cancelKeep")}
        closeLabel={t("close")}
        destructive
        loading={cancel.isPending}
        onConfirm={doCancel}
      >
        <Field label={t("cancelReason")} htmlFor="cancel-reason" hint={t("optional")}>
          <Textarea id="cancel-reason" rows={3} maxLength={300} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
      </ConfirmDialog>
    </>
  );
}
