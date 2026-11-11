"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { CheckCircle2, Clock, Lock, RefreshCw } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Label, RadioGroup, RadioItem } from "@/components/ui/form";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { useMoney, toNumber } from "@/modules/catalog/format";
import { useTracking } from "@/modules/orders/api";
import { trackingLink } from "@/modules/orders/status";
import { useInitiatePayment, usePaymentMethods } from "./api";
import { MethodIcon, usePayMethodLabel, usePaymentErrorText, useSessionStorage } from "./common";
import { ManualTransferForm } from "./manual-transfer-form";
import { useOrderAccess } from "./order-access";
import { isOnlineMethod, payPath, returnPath, sandboxPath, savePayState } from "./status";

/**
 * `/pay/[number]`: the page that takes an order to its payment. For an online method it starts the payment (the shop makes a
 * payment session) and sends the browser to the gateway's page (the sandbox's own branded page for the test method). For a manual
 * transfer it shows the account details and the form. A failed or cancelled payment shows a "try again" choice here.
 */
export function PayView({ number, tokenParam, retry }: { number: string; tokenParam?: string; retry: boolean }) {
  const t = useTranslations("payments.pay");
  const router = useRouter();
  const money = useMoney();
  const methodLabel = usePayMethodLabel();
  const errorText = usePaymentErrorText();
  const storage = useSessionStorage();
  const access = useOrderAccess(number, tokenParam);
  const tracking = useTracking(access.query);
  const initiate = useInitiatePayment();
  const order = tracking.data;
  const methods = usePaymentMethods(order ? toNumber(order.grandTotal) : 0, !!order);
  const [choice, setChoice] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const started = React.useRef(false);

  const online = order ? isOnlineMethod(order.paymentMethod) : false;
  const open = !!order && !["Cancelled", "Returned", "DeliveryFailed"].includes(order.status);
  const waiting = open && online && order.paymentStatus === "Pending";
  const again = open && online && (order.paymentStatus === "Failed" || order.paymentStatus === "Cancelled");
  const autoStart = waiting || (again && retry);

  const start = React.useCallback(
    async (method: string | null) => {
      setError(null);
      try {
        const session = await initiate.mutateAsync({ orderNumber: number, token: access.token, method, returnUrl: returnPath(number) });
        savePayState(storage, number, { paymentId: session.paymentId, token: access.token, deadline: access.stored?.paymentDeadline ?? null });
        if (session.sandboxSessionId) router.replace(sandboxPath(session.sandboxSessionId, session.paymentId));
        else if (session.redirectUrl) window.location.assign(session.redirectUrl);
        else setError(errorText(null));
      } catch (failure) {
        started.current = false;
        setError(errorText(failure));
      }
    },
    // The mutation object changes every render; what matters is the order and how it is proven.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [number, access.token, storage, router],
  );

  React.useEffect(() => {
    if (!autoStart || started.current || !storage) return;
    started.current = true;
    void start(null);
  }, [autoStart, storage, start]);

  if (access.settling || (tracking.isPending && !!access.query)) return <Skeleton className="h-64" aria-busy />;

  if (!access.query || tracking.isError || !order) {
    const notFound = tracking.error instanceof ApiError && tracking.error.problem.status === 404;
    return (
      <Card className="space-y-4 p-5" data-testid="pay-no-access">
        <Alert variant="warning" title={notFound ? t("notFound") : t("noAccess")}>
          {notFound ? t("notFoundText") : t("noAccessText")}
        </Alert>
        <div className="flex flex-wrap gap-2">
          <Link href="/track" className={buttonVariants({ variant: "secondary" })}>
            {t("trackPage")}
          </Link>
          {!access.customer ? (
            <Link href={`/login?as=customer&next=${encodeURIComponent(payPath(number))}`} className={buttonVariants()}>
              {t("signIn")}
            </Link>
          ) : null}
        </div>
      </Card>
    );
  }

  const trackHref = access.token ? trackingLink(access.token) : "/account/orders";
  const header = (
    <div className="space-y-1 text-center">
      <h1 className="font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>
      <p className="text-lg text-strong">
        {t("order")}: <strong className="font-mono" data-testid="pay-order-number">{order.number}</strong>
      </p>
      <p className="text-muted">
        {t("amount")}: <strong className="text-strong" data-testid="pay-amount">{money(toNumber(order.grandTotal))}</strong> · {methodLabel(order.paymentMethod)}
      </p>
    </div>
  );

  // Nothing to pay here.
  if (!open) {
    return (
      <div className="space-y-6">
        {header}
        <Alert variant="warning" data-testid="pay-closed">
          {t("closed")}
        </Alert>
        <Link href={trackHref} className={buttonVariants({ variant: "secondary", size: "lg", className: "w-full" })}>
          {t("viewOrder")}
        </Link>
      </div>
    );
  }
  if (order.paymentMethod === "COD") {
    return (
      <div className="space-y-6">
        {header}
        <Alert variant="info" data-testid="pay-cod">
          {t("cod")}
        </Alert>
        <Link href={trackHref} className={buttonVariants({ variant: "secondary", size: "lg", className: "w-full" })}>
          {t("viewOrder")}
        </Link>
      </div>
    );
  }
  if (["Paid", "PartiallyRefunded", "Refunded", "Settled", "CollectedByCourier"].includes(order.paymentStatus)) {
    return (
      <div className="space-y-6">
        {header}
        <Alert variant="success" data-testid="pay-paid">
          <CheckCircle2 className="me-1 inline size-4" aria-hidden /> {t("alreadyPaid")}
        </Alert>
        <Link href={trackHref} className={buttonVariants({ size: "lg", className: "w-full" })}>
          {t("viewOrder")}
        </Link>
      </div>
    );
  }
  if (order.paymentMethod === "MANUAL" || order.paymentStatus === "UnderReview") {
    return (
      <div className="space-y-6">
        {header}
        <ManualTransferForm number={number} token={access.token} amount={toNumber(order.grandTotal)} reviewing={order.paymentStatus === "UnderReview"} trackHref={trackHref} />
      </div>
    );
  }

  const onlineMethods = (methods.data ?? []).filter((m) => isOnlineMethod(m.code));
  const selected = choice ?? order.paymentMethod;

  return (
    <div className="space-y-6">
      {header}
      {again && !retry ? (
        <Card className="space-y-4 p-4 sm:p-5" data-testid="pay-again">
          <Alert variant={order.paymentStatus === "Failed" ? "danger" : "warning"} title={order.paymentStatus === "Failed" ? t("failedTitle") : t("cancelledTitle")}>
            {t("againText")}
          </Alert>
          {onlineMethods.length > 1 ? (
            <RadioGroup value={selected} onValueChange={setChoice} className="space-y-2" aria-label={t("chooseMethod")}>
              {onlineMethods.map((m) => (
                <div key={m.code} className="flex items-start gap-3 rounded-control border border-line bg-card p-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft has-[[data-disabled]]:opacity-60">
                  <RadioItem id={`pay-m-${m.code}`} value={m.code} disabled={!m.available} className="mt-0.5" />
                  <Label htmlFor={`pay-m-${m.code}`} className="min-h-11 flex-1 cursor-pointer font-normal">
                    <span className="flex items-center gap-2 font-semibold text-strong">
                      <MethodIcon code={m.code} className="size-5 text-primary" /> {methodLabel(m.code, m.name)}
                    </span>
                    {!m.available && m.reason ? <span className="block text-sm text-muted">{m.reason}</span> : null}
                  </Label>
                </div>
              ))}
            </RadioGroup>
          ) : null}
          {error ? <Alert variant="danger" data-testid="pay-error">{error}</Alert> : null}
          <Button size="lg" className="w-full" loading={initiate.isPending} onClick={() => void start(selected === order.paymentMethod ? null : selected)} data-testid="pay-retry">
            <RefreshCw aria-hidden /> {t("tryAgain")}
          </Button>
        </Card>
      ) : (
        <Card className="space-y-4 p-4 text-center sm:p-5" aria-live="polite" data-testid="pay-starting">
          {error ? (
            <>
              <Alert variant="danger" data-testid="pay-error">
                {error}
              </Alert>
              <Button size="lg" className="w-full" loading={initiate.isPending} onClick={() => void start(null)}>
                <RefreshCw aria-hidden /> {t("tryAgain")}
              </Button>
            </>
          ) : (
            <>
              <Clock className="mx-auto size-10 text-primary" aria-hidden />
              <p className="font-semibold text-strong">{t("starting")}</p>
              <p className="flex items-center justify-center gap-2 text-sm text-muted">
                <Lock className="size-4" aria-hidden /> {t("secure")}
              </p>
            </>
          )}
        </Card>
      )}
      <p className="text-center">
        <Link href={trackHref} className="inline-flex min-h-11 items-center font-semibold text-strong underline underline-offset-4">
          {t("payLater")}
        </Link>
      </p>
    </div>
  );
}
