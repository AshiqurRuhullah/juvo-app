"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Ban, CheckCircle2, Clock, Hourglass, RefreshCw, XCircle } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { useMoney, toNumber } from "@/modules/catalog/format";
import { trackingLink } from "@/modules/orders/status";
import { useConfirmPayment, usePaymentStatus } from "./api";
import { usePayMethodLabel, usePaymentErrorText, useNow, useSessionStorage } from "./common";
import { useOrderAccess } from "./order-access";
import { countdown, loadPayState, outcomeOf, payPath, secondsLeft } from "./status";

/**
 * `/pay/return`: where the gateway sends the browser back to. Nothing in the address is trusted: the page only uses it to find
 * which order this was. It asks the shop's server to check the payment with the gateway (confirm), then shows what the shop's own
 * records say, polling while the answer is still "pending". A failed or cancelled payment offers a new attempt while the 30 minute
 * hold of the order runs.
 */
export function PayReturnView({ number, paymentParam }: { number: string; paymentParam?: string }) {
  const t = useTranslations("payments.return");
  const money = useMoney();
  const methodLabel = usePayMethodLabel();
  const errorText = usePaymentErrorText();
  const storage = useSessionStorage();
  const now = useNow();
  const access = useOrderAccess(number);
  const state = React.useMemo(() => loadPayState(storage, number), [storage, number]);
  const paymentId = state?.paymentId ?? paymentParam ?? null;
  const token = state?.token ?? access.token;
  const deadline = state?.deadline ?? access.stored?.paymentDeadline ?? null;

  const confirm = useConfirmPayment();
  const status = usePaymentStatus(paymentId, token, true);
  const asked = React.useRef(false);

  React.useEffect(() => {
    if (!paymentId || asked.current || !storage) return;
    asked.current = true;
    confirm.mutate({ paymentId, token });
    // Asked once per visit; the poll below keeps the page current.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [paymentId, token, storage]);

  const session = status.data ?? confirm.data;
  const outcome = outcomeOf(session);
  const trackHref = token ? trackingLink(token) : "/account/orders";
  const left = secondsLeft(deadline, now);
  const timerShown = left !== null && (outcome === "failed" || outcome === "cancelled" || outcome === "pending");

  if (!storage || (access.settling && !paymentId)) return <Skeleton className="h-64" aria-busy />;

  if (!paymentId) {
    return (
      <Card className="space-y-4 p-5" data-testid="return-unknown">
        <Alert variant="warning" title={t("unknownTitle")}>
          {t("unknownText")}
        </Alert>
        <div className="flex flex-wrap gap-2">
          <Link href="/track" className={buttonVariants({ variant: "secondary" })}>
            {t("trackPage")}
          </Link>
          <Link href={payPath(number, token)} className={buttonVariants()}>
            {t("openPay")}
          </Link>
        </div>
      </Card>
    );
  }

  const failedToLoad = status.isError && confirm.isError && !session;

  return (
    <div className="space-y-6" aria-live="polite">
      <div className="space-y-2 text-center">
        {outcome === "paid" ? <CheckCircle2 className="mx-auto size-14 text-success" aria-hidden /> : null}
        {outcome === "failed" ? <XCircle className="mx-auto size-14 text-danger" aria-hidden /> : null}
        {outcome === "cancelled" ? <Ban className="mx-auto size-14 text-warning" aria-hidden /> : null}
        {outcome === "review" ? <Hourglass className="mx-auto size-14 text-primary" aria-hidden /> : null}
        {outcome === "pending" ? <Clock className="mx-auto size-14 text-primary" aria-hidden /> : null}
        <h1 className="font-display text-2xl font-semibold text-strong sm:text-3xl" data-testid="return-title" data-outcome={outcome}>
          {t(`title.${outcome}`)}
        </h1>
        <p className="text-muted">{t(`text.${outcome}`)}</p>
        <p className="text-lg text-strong">
          {t("order")}: <strong className="font-mono" data-testid="return-order">{number}</strong>
        </p>
        {session ? (
          <p className="text-muted">
            {money(toNumber(session.amount))} · {methodLabel(session.method)}
          </p>
        ) : null}
      </div>

      {failedToLoad ? <Alert variant="danger" data-testid="return-error">{errorText(status.error ?? confirm.error)}</Alert> : null}
      {session?.failureReason && (outcome === "failed" || outcome === "cancelled") ? <Alert variant="warning">{session.failureReason}</Alert> : null}
      {session && outcome === "paid" && session.orderStatus === "Confirmed" ? <Alert variant="success" data-testid="return-confirmed">{t("orderConfirmed")}</Alert> : null}

      {timerShown ? (
        <Alert variant={left === 0 ? "danger" : "warning"} data-testid="hold-timer">
          {left === 0 ? t("holdOver") : t("hold", { time: countdown(left) })}
        </Alert>
      ) : null}

      <div className="flex flex-col gap-2">
        {outcome === "pending" ? (
          <Button size="lg" variant="secondary" loading={confirm.isPending || status.isFetching} onClick={() => confirm.mutate({ paymentId, token })} data-testid="return-check">
            <RefreshCw aria-hidden /> {t("checkAgain")}
          </Button>
        ) : null}
        {outcome === "failed" || outcome === "cancelled" ? (
          <Link href={payPath(number, token, true)} className={buttonVariants({ size: "lg", className: "w-full" })} data-testid="return-retry">
            <RefreshCw aria-hidden /> {t("retry")}
          </Link>
        ) : null}
        <Link href={trackHref} className={buttonVariants({ size: "lg", variant: outcome === "paid" || outcome === "review" ? "primary" : "secondary", className: "w-full" })}>
          {t("viewOrder")}
        </Link>
        <Link href="/shop" className="inline-flex min-h-11 items-center justify-center font-semibold text-strong underline underline-offset-4">
          {t("continue")}
        </Link>
      </div>
    </div>
  );
}
