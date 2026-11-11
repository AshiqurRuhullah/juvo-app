"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Ban, CircleCheck, CircleX, FlaskConical } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useSandboxDecision, useSandboxSession, type SandboxOutcome } from "./api";
import { useNow, usePaymentErrorText } from "./common";
import { returnPath, withParam } from "./status";

/**
 * The shop's own pay page for the SANDBOX method (the API's session JSON, branded). The three buttons stand in for what a real
 * gateway page would let the customer do. Choosing one makes the sandbox call the shop's webhook (signed, server to server) and
 * sends the browser back to the shop; the return page then asks the server for the truth.
 */
export function SandboxPayView({ sessionId, paymentId }: { sessionId: string; paymentId?: string }) {
  const t = useTranslations("payments.sandbox");
  const router = useRouter();
  const money = useMoney();
  const dateTime = useDateTime();
  const errorText = usePaymentErrorText();
  const now = useNow();
  const session = useSandboxSession(sessionId);
  const decide = useSandboxDecision(sessionId);
  const [error, setError] = React.useState<string | null>(null);
  const [chosen, setChosen] = React.useState<SandboxOutcome | null>(null);

  if (session.isPending) return <Skeleton className="h-72" aria-busy />;
  if (session.isError || !session.data) {
    const gone = session.error instanceof ApiError && session.error.problem.status === 404;
    return (
      <Card className="space-y-4 p-5" data-testid="sandbox-missing">
        <Alert variant="danger" title={gone ? t("notFound") : t("loadFailed")}>
          {gone ? t("notFoundText") : errorText(session.error)}
        </Alert>
        <Link href="/track" className={buttonVariants({ variant: "secondary" })}>
          {t("trackPage")}
        </Link>
      </Card>
    );
  }

  const data = session.data;
  const usable = data.status === "Pending" && new Date(data.expiresAt).getTime() > now;
  const back = data.returnUrl;
  const goBack = (target: string | null | undefined) => {
    const base = withParam(target, "order", data.orderNumber, returnPath(data.orderNumber));
    router.push(paymentId ? withParam(base, "payment", paymentId, base) : base);
  };

  const choose = async (outcome: SandboxOutcome) => {
    setError(null);
    setChosen(outcome);
    try {
      const result = await decide.mutateAsync(outcome);
      goBack(result.returnUrl);
    } catch (failure) {
      setChosen(null);
      setError(errorText(failure));
    }
  };

  return (
    <div className="space-y-5">
      <div className="space-y-2 text-center">
        <FlaskConical className="mx-auto size-12 text-primary" aria-hidden />
        <h1 className="font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>
        <p className="text-muted">{t("subtitle")}</p>
      </div>
      <Card className="space-y-4 p-4 sm:p-5">
        <dl className="space-y-2 text-sm">
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t("order")}</dt>
            <dd className="font-mono font-semibold text-strong" data-testid="sandbox-order">{data.orderNumber}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t("amount")}</dt>
            <dd className="text-base font-semibold text-strong" data-testid="sandbox-amount">{money(toNumber(data.amount))}</dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t("validUntil")}</dt>
            <dd className="text-strong">{dateTime(data.expiresAt)}</dd>
          </div>
        </dl>
        {usable ? (
          <div className="space-y-3">
            <p className="text-sm text-muted">{t("choose")}</p>
            <Button size="lg" className="w-full" loading={decide.isPending && chosen === "success"} disabled={decide.isPending} onClick={() => void choose("success")} data-testid="sandbox-success">
              <CircleCheck aria-hidden /> {t("success")}
            </Button>
            <Button size="lg" variant="secondary" className="w-full" loading={decide.isPending && chosen === "fail"} disabled={decide.isPending} onClick={() => void choose("fail")} data-testid="sandbox-fail">
              <CircleX aria-hidden /> {t("fail")}
            </Button>
            <Button size="lg" variant="ghost" className="w-full" loading={decide.isPending && chosen === "cancel"} disabled={decide.isPending} onClick={() => void choose("cancel")} data-testid="sandbox-cancel">
              <Ban aria-hidden /> {t("cancel")}
            </Button>
            {error ? <Alert variant="danger" data-testid="sandbox-error">{error}</Alert> : null}
          </div>
        ) : (
          <div className="space-y-3">
            <Alert variant="warning" data-testid="sandbox-used">
              {data.status === "Pending" ? t("expired") : t("used", { status: t(`status.${data.status}`) })}
            </Alert>
            <Button size="lg" className="w-full" onClick={() => goBack(back)}>
              {t("backToShop")}
            </Button>
          </div>
        )}
      </Card>
      <p className="text-center text-sm text-muted">{t("note")}</p>
    </div>
  );
}
