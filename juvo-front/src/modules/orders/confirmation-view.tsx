"use client";

import * as React from "react";
import Link from "next/link";
import { useFormatter, useTranslations } from "next-intl";
import { CalendarClock, CheckCircle2, Copy, Search, UserPlus } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Alert, Card } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useStoreSession, signInUrl } from "@/modules/store/store-session";
import type { CheckoutResult } from "./api";
import { ORDER_STORAGE_PREFIX } from "./checkout-view";
import { AmountList, useMethodLabel } from "./common";
import { trackingLink } from "./status";

const subscribe = () => () => undefined;

/** The order the checkout placed in this browser tab (kept in session storage; the tracking token is shown only from here). */
function useStoredResult(number: string): CheckoutResult | null {
  const raw = React.useSyncExternalStore(
    subscribe,
    () => {
      try {
        return window.sessionStorage.getItem(`${ORDER_STORAGE_PREFIX}${number}`);
      } catch {
        return null;
      }
    },
    () => null,
  );
  return React.useMemo(() => {
    try {
      return raw ? (JSON.parse(raw) as CheckoutResult) : null;
    } catch {
      return null;
    }
  }, [raw]);
}

/**
 * Thank-you page after checkout: the order number, what to pay and when it arrives, the tracking link (the API gives the token only
 * once, so it is shown here from this tab's storage and the person is asked to keep it) and, for guests, an invitation to create a profile.
 */
export function ConfirmationView({ number }: { number: string }) {
  const t = useTranslations("orders.confirmed");
  const money = useMoney();
  const format = useFormatter();
  const methodLabel = useMethodLabel();
  const { customer } = useStoreSession();
  const result = useStoredResult(number);
  const origin = React.useSyncExternalStore(subscribe, () => window.location.origin, () => "");

  const link = result?.trackingToken ? trackingLink(result.trackingToken) : null;
  const copy = async () => {
    if (!link) return;
    try {
      await navigator.clipboard.writeText(`${origin}${link}`);
      toast.success(t("copied"));
    } catch {
      toast.error(t("copyFailed"));
    }
  };
  const date = (value: string | null | undefined) => (value ? format.dateTime(new Date(value), { dateStyle: "medium" }) : null);
  const advance = result ? toNumber(result.advanceRequired) : 0;
  const online = !!result && result.paymentMethod !== "COD";

  return (
    <div className="space-y-6">
      <div className="space-y-3 text-center">
        <CheckCircle2 className="mx-auto size-14 text-success" aria-hidden />
        <h1 className="font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>
        <p className="text-muted">{t("subtitle")}</p>
        <p className="text-lg text-strong">
          {t("number")}: <strong className="font-mono" data-testid="order-number">{number}</strong>
        </p>
      </div>

      {result ? (
        <Card className="space-y-4 p-4 sm:p-5" data-testid="order-summary">
          <h2 className="text-lg font-semibold text-strong">{t("summary")}</h2>
          <AmountList
            rows={[
              { key: "total", label: t("total"), value: result.grandTotal, always: true, strong: true },
              { key: "paynow", label: t("payNow"), value: result.payNow, always: advance > 0 || online },
              { key: "ondelivery", label: t("onDelivery"), value: result.payOnDelivery, always: advance > 0 || !online },
            ]}
          />
          <p className="text-sm text-muted">{t("method", { method: methodLabel(result.paymentMethod) })}</p>
          {advance > 0 ? <Alert variant="warning">{t("advance", { amount: money(result.payNow) })}</Alert> : null}
          {online && result.paymentDeadline ? <Alert variant="warning">{t("deadline", { time: format.dateTime(new Date(result.paymentDeadline), { dateStyle: "medium", timeStyle: "short" }) })}</Alert> : null}
          {result.earliestDelivery ? (
            <p className="flex items-start gap-2 text-sm text-muted">
              <CalendarClock className="mt-0.5 size-4 shrink-0" aria-hidden />
              <span data-testid="delivery-window">{t("window", { from: date(result.earliestDelivery) ?? "", to: date(result.latestDelivery ?? result.earliestDelivery) ?? "" })}</span>
            </p>
          ) : null}
        </Card>
      ) : null}

      {link ? (
        <Card className="space-y-3 p-4 sm:p-5" data-testid="tracking-box">
          <h2 className="flex items-center gap-2 text-lg font-semibold text-strong">
            <Search className="size-5 text-primary" aria-hidden /> {t("trackTitle")}
          </h2>
          <p className="text-sm text-muted">{t("trackOnce")}</p>
          <p className="rounded-control border border-line bg-sunken p-3 font-mono text-sm text-strong [overflow-wrap:anywhere]" data-testid="tracking-link">
            {origin}
            {link}
          </p>
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" onClick={() => void copy()}>
              <Copy aria-hidden /> {t("copy")}
            </Button>
            <Link href={link} className={buttonVariants()} data-testid="track-now">
              {t("trackNow")}
            </Link>
          </div>
        </Card>
      ) : (
        <Card className="space-y-3 p-4 sm:p-5">
          <p className="text-muted">{t("trackByNumber")}</p>
          <div>
            <Link href="/track" className={buttonVariants({ variant: "secondary" })}>
              <Search aria-hidden /> {t("trackPage")}
            </Link>
          </div>
        </Card>
      )}

      {result?.offerProfile && !customer ? (
        <Card className="space-y-3 p-4 sm:p-5" data-testid="profile-offer">
          <h2 className="flex items-center gap-2 text-lg font-semibold text-strong">
            <UserPlus className="size-5 text-primary" aria-hidden /> {t("profileTitle")}
          </h2>
          <p className="text-muted">{t("profileText")}</p>
          <div>
            <Link href={signInUrl("/account/orders")} className={buttonVariants()} data-testid="create-profile">
              {t("profileCta")}
            </Link>
          </div>
        </Card>
      ) : null}

      <div className="text-center">
        <Link href="/shop" className={buttonVariants({ variant: "secondary", size: "lg" })}>
          {t("continue")}
        </Link>
      </div>
    </div>
  );
}
