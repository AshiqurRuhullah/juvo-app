"use client";

import * as React from "react";
import Image from "next/image";
import { useTranslations } from "next-intl";
import { Ban, Check, CircleDot, Flag, ShieldAlert } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { mediaUrl, toNumber, useMoney } from "@/modules/catalog/format";
import { usePickName } from "@/modules/store/hooks";
import type { DeliveryStatus, FraudState, OrderLine, OrderSource, OrderStatus, PaymentStatus, RiskFlag, TimelineEntry } from "./api";
import { deliveryStatusTone, fraudStateTone, orderStatusTone, paymentStatusTone, progressSteps, trackEntries } from "./status";

export function OrderStatusBadge({ status }: { status: OrderStatus }) {
  const t = useTranslations("orders.status.order");
  return <Badge variant={orderStatusTone(status)}>{t(status)}</Badge>;
}

export function PaymentStatusBadge({ status }: { status: PaymentStatus }) {
  const t = useTranslations("orders.status.payment");
  return <Badge variant={paymentStatusTone(status)}>{t(status)}</Badge>;
}

export function DeliveryStatusBadge({ status }: { status: DeliveryStatus }) {
  const t = useTranslations("orders.status.delivery");
  return <Badge variant={deliveryStatusTone(status)}>{t(status)}</Badge>;
}

/** Shows the fraud state only when something needs a person's eye: danger states carry an icon and a text. */
export function FraudBadge({ state, className }: { state: FraudState; className?: string }) {
  const t = useTranslations("orders.status.fraud");
  if (state === "None") return null;
  const Icon = state === "Rejected" ? Ban : state === "Approved" ? Check : ShieldAlert;
  return (
    <Badge variant={fraudStateTone(state)} className={className}>
      <Icon className="size-3.5" aria-hidden /> {t(state)}
    </Badge>
  );
}

export function useSourceLabel() {
  const t = useTranslations("orders.source");
  return (source: OrderSource) => t(source);
}

export function useMethodLabel() {
  const t = useTranslations("orders.method");
  const pay = useTranslations("payments.method");
  return (method: string) => (t.has(method) ? t(method) : pay.has(method) ? pay(method) : method);
}

/** The reasons an order was flagged, one chip each with the rule's own message. */
export function RiskChips({ flags, max }: { flags: RiskFlag[]; max?: number }) {
  const t = useTranslations("orders.common");
  const shown = max ? flags.slice(0, max) : flags;
  if (flags.length === 0) return null;
  return (
    <ul className="flex flex-wrap gap-1.5" aria-label={t("flags")}>
      {shown.map((f) => (
        <li key={`${f.ruleCode}-${f.at}`}>
          <Badge variant="danger" className="max-w-full whitespace-normal [overflow-wrap:anywhere]">
            <Flag className="size-3.5 shrink-0" aria-hidden /> {f.message}
          </Badge>
        </li>
      ))}
      {max && flags.length > max ? (
        <li>
          <Badge variant="outline">+{flags.length - max}</Badge>
        </li>
      ) : null}
    </ul>
  );
}

/** The items of an order with the name, price and quantity as they were when it was placed. */
export function OrderLines({ lines, label }: { lines: OrderLine[]; label: string }) {
  const money = useMoney();
  const pick = usePickName();
  const t = useTranslations("orders.common");
  return (
    <ul className="divide-y divide-line" aria-label={label} data-testid="order-lines">
      {lines.map((line) => {
        const image = mediaUrl(line.imageUrl);
        const name = pick(line.name, line.nameBn);
        return (
          <li key={line.variantId} className="flex gap-3 py-3" data-sku={line.sku}>
            <span className="relative block aspect-[4/5] w-14 shrink-0 overflow-hidden rounded-control bg-sunken">{image ? <Image src={image} alt="" fill sizes="56px" className="object-cover" /> : null}</span>
            <div className="min-w-0 flex-1">
              <p className="font-medium text-strong [overflow-wrap:anywhere]">{name}</p>
              <p className="text-sm text-muted [overflow-wrap:anywhere]">{[line.sku, line.colorName, line.size ? t("size", { size: line.size }) : null].filter(Boolean).join(" · ")}</p>
              <p className="text-sm text-muted">
                {money(line.unitPrice)} × {toNumber(line.quantity)}
              </p>
            </div>
            <p className="shrink-0 font-semibold text-strong">{money(line.lineTotal)}</p>
          </li>
        );
      })}
    </ul>
  );
}

/** Amount rows (label left, amount right). Rows with a zero amount are left out unless `always` is set. */
export function AmountList({ rows, className }: { rows: { key: string; label: string; value: number | string; negative?: boolean; strong?: boolean; always?: boolean; text?: string }[]; className?: string }) {
  const money = useMoney();
  return (
    <dl className={cn("space-y-2 text-sm", className)}>
      {rows
        .filter((r) => r.always || r.strong || toNumber(r.value) !== 0)
        .map((r) => (
          <div key={r.key} className={cn("flex justify-between gap-3", r.strong ? "border-t border-line pt-3 text-base font-semibold" : "")}>
            <dt className={r.strong ? "text-strong" : "text-muted"}>{r.label}</dt>
            <dd className={cn("text-end", r.strong ? "text-strong" : "text-strong", r.negative ? "text-success-ink" : "")} data-testid={`amount-${r.key}`}>
              {r.text ?? `${r.negative ? "-" : ""}${money(r.value)}`}
            </dd>
          </div>
        ))}
    </dl>
  );
}

/**
 * The progress line of an order: the steps of the normal road with ticks for the ones reached, the current step marked, and an
 * end status (cancelled, delivery failed, returned) when the order left the road. Meaning is in text and icons, not colour alone.
 */
export function ProgressLine({ status, timeline }: { status: OrderStatus; timeline: TimelineEntry[] }) {
  const t = useTranslations("orders.status.order");
  const tp = useTranslations("orders.common");
  const dateTime = useDateTime();
  const { steps, extra } = progressSteps(status, timeline);
  return (
    <ol className="space-y-3" aria-label={tp("progress")} data-testid="progress-line">
      {steps.map((step) => (
        <li key={step.status} className="flex items-start gap-3" aria-current={step.state === "current" ? "step" : undefined} data-state={step.state}>
          {step.state === "done" ? (
            <Check className="mt-0.5 size-5 shrink-0 rounded-full bg-success p-0.5 text-card" aria-hidden />
          ) : step.state === "current" ? (
            <CircleDot className="mt-0.5 size-5 shrink-0 text-primary" aria-hidden />
          ) : (
            <span className="mt-0.5 size-5 shrink-0 rounded-full border-2 border-line" aria-hidden />
          )}
          <div className="min-w-0">
            <p className={cn(step.state === "todo" ? "text-muted" : "font-semibold text-strong")}>
              {t(step.status)}
              {step.state === "done" ? <span className="sr-only"> ({tp("done")})</span> : step.state === "current" ? <span className="sr-only"> ({tp("current")})</span> : null}
            </p>
            {step.at ? <p className="text-sm text-muted">{dateTime(step.at)}</p> : null}
          </div>
        </li>
      ))}
      {extra ? (
        <li className="flex items-start gap-3" aria-current="step" data-state="end">
          <Ban className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden />
          <div className="min-w-0">
            <p className="font-semibold text-strong">{t(extra.status)}</p>
            {extra.at ? <p className="text-sm text-muted">{dateTime(extra.at)}</p> : null}
          </div>
        </li>
      ) : null}
    </ol>
  );
}

/** One track's history as a list: what it became, when, by whom (staff view only) and the reason. */
export function TrackHistory({ timeline, track, showActor = false }: { timeline: TimelineEntry[]; track: "Order" | "Payment" | "Delivery"; showActor?: boolean }) {
  const to = useTranslations(`orders.status.${track === "Order" ? "order" : track === "Payment" ? "payment" : "delivery"}`);
  const tc = useTranslations("orders.common");
  const dateTime = useDateTime();
  const rows = trackEntries(timeline, track);
  if (rows.length === 0) return <p className="text-sm text-muted">{tc("noHistory")}</p>;
  return (
    <ol className="space-y-3" data-testid={`history-${track.toLowerCase()}`}>
      {rows.map((row, index) => (
        <li key={`${row.at}-${row.to}-${index}`} className="flex items-start gap-3">
          <span className="mt-1.5 size-2.5 shrink-0 rounded-full bg-primary" aria-hidden />
          <div className="min-w-0">
            <p className="font-medium text-strong">
              {row.from && to.has(row.from) ? <span className="text-muted">{to(row.from as never)} → </span> : null}
              {to.has(row.to) ? to(row.to as never) : row.to}
            </p>
            <p className="text-sm text-muted">
              {dateTime(row.at, "long")}
              {showActor && row.actorName ? ` · ${row.actorName}` : ""}
            </p>
            {row.reason ? <p className="text-sm text-muted [overflow-wrap:anywhere]">{row.reason}</p> : null}
          </div>
        </li>
      ))}
    </ol>
  );
}

/** The three parallel tracks of an order side by side: order, payment, delivery. */
export function TrackBadges({ status, paymentStatus, deliveryStatus }: { status: OrderStatus; paymentStatus: PaymentStatus; deliveryStatus: DeliveryStatus }) {
  const t = useTranslations("orders.common");
  return (
    <dl className="grid gap-3 sm:grid-cols-3" data-testid="order-tracks">
      <div className="space-y-1">
        <dt className="text-sm text-muted">{t("orderTrack")}</dt>
        <dd data-testid="status-order">
          <OrderStatusBadge status={status} />
        </dd>
      </div>
      <div className="space-y-1">
        <dt className="text-sm text-muted">{t("paymentTrack")}</dt>
        <dd data-testid="status-payment">
          <PaymentStatusBadge status={paymentStatus} />
        </dd>
      </div>
      <div className="space-y-1">
        <dt className="text-sm text-muted">{t("deliveryTrack")}</dt>
        <dd data-testid="status-delivery">
          <DeliveryStatusBadge status={deliveryStatus} />
        </dd>
      </div>
    </dl>
  );
}

/** A small card-like block with a heading, used by every order screen. */
export function Section({ title, children, className, id }: { title: string; children: React.ReactNode; className?: string; id?: string }) {
  return (
    <section aria-labelledby={id} className={cn("rounded-card border border-line bg-card p-4 sm:p-5", className)}>
      <h2 id={id} className="mb-3 text-lg font-semibold text-strong">
        {title}
      </h2>
      {children}
    </section>
  );
}
