"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { signInUrl, useStoreSession } from "@/modules/store/store-session";
import { GuestInvoicePanel, PaymentPanel } from "@/modules/payments/customer-panels";
import { useOrderErrorText, useTracking, type TrackQuery } from "./api";
import { OrderLines, ProgressLine, Section, TrackBadges, TrackHistory } from "./common";
import { cleanMobile, isBdMobile } from "./status";

/**
 * Public order tracking. A person can look an order up with the order number and the mobile used, or open the private link given
 * at checkout (`?token=`). The answer shows the three tracks, the progress and the history; it never has the street address or staff names.
 */
export function TrackView({ initialToken, initialNumber, initialMobile }: { initialToken?: string; initialNumber?: string; initialMobile?: string }) {
  const t = useTranslations("orders.track");
  const money = useMoney();
  const dateTime = useDateTime();
  const errorText = useOrderErrorText();
  const { customer } = useStoreSession();
  const [number, setNumber] = React.useState(initialNumber ?? "");
  const [mobile, setMobile] = React.useState(initialMobile ?? "");
  const [errors, setErrors] = React.useState<{ number?: string; mobile?: string }>({});
  const [query, setQuery] = React.useState<TrackQuery | null>(initialToken ? { token: initialToken } : initialNumber && initialMobile ? { number: initialNumber.trim(), mobile: cleanMobile(initialMobile) } : null);
  const tracking = useTracking(query);

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const next: typeof errors = {};
    if (!number.trim()) next.number = t("errors.number");
    if (!isBdMobile(mobile)) next.mobile = t("errors.mobile");
    setErrors(next);
    if (next.number || next.mobile) return;
    setQuery({ number: number.trim().toUpperCase(), mobile: cleanMobile(mobile) });
  };

  const data = tracking.data;
  const notFound = tracking.error instanceof ApiError && tracking.error.problem.status === 404;

  return (
    <div className="space-y-6">
      {!initialToken ? (
        <Card className="p-4 sm:p-5">
          <form onSubmit={submit} noValidate className="space-y-4" aria-label={t("formLabel")}>
            <p className="text-muted">{t("help")}</p>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t("number")} htmlFor="track-number" required hint={t("numberHint")} error={errors.number}>
                <Input id="track-number" value={number} onChange={(e) => setNumber(e.target.value)} autoComplete="off" aria-invalid={!!errors.number} />
              </Field>
              <Field label={t("mobile")} htmlFor="track-mobile" required hint={t("mobileHint")} error={errors.mobile}>
                <Input id="track-mobile" type="tel" inputMode="tel" autoComplete="tel" value={mobile} onChange={(e) => setMobile(e.target.value)} aria-invalid={!!errors.mobile} />
              </Field>
            </div>
            <Button type="submit" size="lg" loading={tracking.isFetching}>
              <Search aria-hidden /> {t("submit")}
            </Button>
          </form>
        </Card>
      ) : null}

      {tracking.isFetching && !data ? <Skeleton className="h-64" aria-busy /> : null}
      {tracking.isError ? (
        <Alert variant="danger" data-testid="track-error">
          {notFound ? t("notFound") : errorText(tracking.error)}
        </Alert>
      ) : null}

      {data && !tracking.isError ? (
        <div className="space-y-5" data-testid="track-result">
          <Card className="space-y-4 p-4 sm:p-5">
            <div className="flex flex-wrap items-baseline justify-between gap-2">
              <h2 className="text-xl font-semibold text-strong">
                {t("order")} <span className="font-mono" data-testid="track-number">{data.number}</span>
              </h2>
              <p className="text-sm text-muted">{t("placed", { date: dateTime(data.placedAt) })}</p>
            </div>
            <TrackBadges status={data.status} paymentStatus={data.paymentStatus} deliveryStatus={data.deliveryStatus} />
            {data.district ? <p className="text-sm text-muted">{t("deliveringTo", { district: data.district })}</p> : null}
            {initialToken || customer ? <PaymentPanel number={data.number} method={data.paymentMethod} paymentStatus={data.paymentStatus} orderStatus={data.status} token={initialToken} /> : null}
          </Card>
          {initialToken ? <GuestInvoicePanel number={data.number} token={initialToken} id="track-invoice" status={data.status} /> : null}

          <div className="grid gap-5 lg:grid-cols-2">
            <Section title={t("progress")} id="track-progress">
              <ProgressLine status={data.status} timeline={data.timeline} />
            </Section>
            <Section title={t("history")} id="track-history" className="space-y-4">
              {(["Order", "Payment", "Delivery"] as const).map((track) => (
                <div key={track} className="space-y-2">
                  <h3 className="text-sm font-semibold text-strong">{t(`track.${track}`)}</h3>
                  <TrackHistory timeline={data.timeline} track={track} />
                </div>
              ))}
            </Section>
          </div>

          <Section title={t("items")} id="track-items">
            <OrderLines lines={data.items} label={t("items")} />
            <p className="mt-3 flex justify-between border-t border-line pt-3 text-base font-semibold text-strong">
              <span>{t("total")}</span>
              <span data-testid="track-total">{money(toNumber(data.grandTotal))}</span>
            </p>
          </Section>

          <Alert variant="info">
            {data.canCancel ? t("cancelHelp") : t("help2")}{" "}
            <Link href={signInUrl("/account/orders")} className="inline-flex min-h-11 items-center font-semibold text-strong underline underline-offset-4">
              {t("signIn")}
            </Link>
          </Alert>
        </div>
      ) : null}
    </div>
  );
}
