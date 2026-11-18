"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { CircleAlert, CircleCheck, MailX } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Alert } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import type { ApiProblem } from "@/lib/api/types";
import { useUnsubscribe, type UnsubscribeResult } from "./api";
import { unsubscribeOutcome } from "./logic";

/**
 * The page an unsubscribe link opens. Opening the link changes nothing (a mail scanner that follows links cannot unsubscribe anyone):
 * the person confirms with a button, which POSTs the token. No sign-in. Works for SMS and email links alike.
 */
export function UnsubscribeView({ token }: { token: string | null }) {
  const t = useTranslations("marketingStore.unsubscribe");
  const unsubscribe = useUnsubscribe();
  const [result, setResult] = React.useState<UnsubscribeResult | null>(null);
  const [problem, setProblem] = React.useState<ApiProblem | null>(null);
  const state = unsubscribeOutcome({ token, result, problem, working: unsubscribe.isPending });

  const confirm = async () => {
    if (!token) return;
    setProblem(null);
    try {
      setResult(await unsubscribe.mutateAsync(token));
    } catch (e) {
      setProblem(e instanceof ApiError ? e.problem : { status: 0, code: "NETWORK" });
    }
  };

  const shop = (
    <Link href="/shop" className={buttonVariants({ variant: "secondary" })}>
      {t("backToShop")}
    </Link>
  );
  const channel = result?.channel === "sms" ? t("channelSms") : result?.channel === "email" ? t("channelEmail") : t("channelAny");

  return (
    <div className="mx-auto w-full max-w-xl px-4 py-8 sm:px-6 sm:py-12" data-testid="unsubscribe-page" data-state={state}>
      <h1 className="mb-4 font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>

      {state === "missing" || state === "invalid" ? (
        <div className="space-y-4 rounded-card border border-line bg-card p-6" role="alert" data-testid="unsubscribe-invalid">
          <p className="flex items-start gap-2 font-semibold text-strong">
            <CircleAlert className="mt-0.5 size-6 shrink-0 text-danger" aria-hidden /> {state === "missing" ? t("missingTitle") : t("invalidTitle")}
          </p>
          <p className="text-muted">{state === "missing" ? t("missingBody") : t("invalidBody")}</p>
          <p className="text-muted">{t("contactHelp")}</p>
          <div className="flex flex-wrap gap-3">
            <Link href="/contact" className={buttonVariants({ variant: "secondary" })}>
              {t("contact")}
            </Link>
            {shop}
          </div>
        </div>
      ) : null}

      {state === "done" || state === "already" ? (
        <div className="space-y-4 rounded-card border border-line bg-card p-6" role="status" data-testid="unsubscribe-done">
          <p className="flex items-start gap-2 font-display text-xl font-semibold text-strong">
            <CircleCheck className="mt-0.5 size-6 shrink-0 text-success" aria-hidden /> {state === "done" ? t("doneTitle") : t("alreadyTitle")}
          </p>
          <p className="text-muted">{state === "done" ? t("doneBody", { channel }) : t("alreadyBody", { channel })}</p>
          <p className="text-muted">{t("orderMessages")}</p>
          {shop}
        </div>
      ) : null}

      {state === "ready" || state === "working" || state === "failed" ? (
        <div className="space-y-4 rounded-card border border-line bg-card p-6" data-testid="unsubscribe-confirm">
          <p className="flex items-start gap-2 font-semibold text-strong">
            <MailX className="mt-0.5 size-6 shrink-0" aria-hidden /> {t("confirmTitle")}
          </p>
          <p className="text-muted">{t("confirmBody")}</p>
          <p className="text-muted">{t("orderMessages")}</p>
          {state === "failed" ? <Alert variant="danger">{problem?.status === 429 ? t("rateLimited") : problem?.status === 0 ? t("network") : t("failed")}</Alert> : null}
          <div className="flex flex-wrap gap-3">
            <Button onClick={() => void confirm()} loading={state === "working"} data-testid="unsubscribe-confirm-button">
              {state === "failed" ? t("tryAgain") : t("confirmButton")}
            </Button>
            {shop}
          </div>
        </div>
      ) : null}
    </div>
  );
}
