"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Copy, Gift, Share2, Sparkles, Ticket, Users, Wallet } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Tabs } from "@/components/ui/tabs";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime } from "@/components/data/list-kit";
import { PAGE_SIZE } from "@/lib/pagination";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useApplyReferral, useMyCredit, useMyPoints, useMyReferral, useMyRewards, useRedeemGiftCard, type MyReferral, type RewardsSummary } from "./api";
import { CreditLedgerTable, PointsLedgerTable, useLoyaltyErrorText } from "./common";
import { earnExample, formatGiftCardCode, isValidGiftCardCode, isValidReferralCode, normalizeCode } from "./logic";

/** `/account/rewards`: points, store credit, history, my referral code and gift card redemption (FR-AB01..AB04). */
export function RewardsView() {
  const t = useTranslations("loyalty.me");
  const rewards = useMyRewards();

  if (rewards.isPending) return <Skeleton className="h-64" aria-busy />;
  if (rewards.isError) return <Alert variant="warning">{t("loadFailed")}</Alert>;
  // Feature off: no rewards for customers. A direct visit gets a calm note, not an error.
  if (!rewards.data) {
    return (
      <EmptyState
        icon={<Gift aria-hidden />}
        title={t("off.title")}
        description={t("off.text")}
        action={
          <Link href="/shop" className={buttonVariants({ variant: "secondary" })}>
            {t("off.cta")}
          </Link>
        }
      />
    );
  }
  return <RewardsBody summary={rewards.data} />;
}

function RewardsBody({ summary }: { summary: RewardsSummary }) {
  const t = useTranslations("loyalty.me");
  const money = useMoney();
  const dateTime = useDateTime();
  const [tab, setTab] = React.useState("points");
  const [pointsPage, setPointsPage] = React.useState(1);
  const [creditPage, setCreditPage] = React.useState(1);
  const points = useMyPoints(pointsPage, PAGE_SIZE);
  const credit = useMyCredit(creditPage, PAGE_SIZE);
  const soon = toNumber(summary.pointsExpiringSoon);
  const rules = summary.rules;
  const example = earnExample(toNumber(rules.earnRatePerBdt));

  return (
    <div className="space-y-5" data-testid="rewards-page">
      <div className="grid gap-3 sm:grid-cols-2">
        <Card className="p-4 sm:p-5" data-testid="points-card">
          <p className="flex items-center gap-2 text-sm text-muted">
            <Sparkles className="size-4 text-primary" aria-hidden /> {t("points")}
          </p>
          <p className="mt-1 text-3xl font-semibold text-strong" data-testid="points-balance">
            {toNumber(summary.pointsBalance)}
          </p>
          <p className="text-sm text-muted">{t("pointsWorth", { amount: money(summary.pointsValue) })}</p>
          {soon > 0 && summary.nextPointsExpiry ? (
            <Alert variant="warning" className="mt-3" data-testid="points-expiring">
              {t("expiring", { points: soon, date: dateTime(summary.nextPointsExpiry) })}
            </Alert>
          ) : summary.nextPointsExpiry ? (
            <p className="mt-2 text-sm text-muted" data-testid="points-next-expiry">
              {t("nextExpiry", { date: dateTime(summary.nextPointsExpiry) })}
            </p>
          ) : null}
        </Card>
        <Card className="p-4 sm:p-5" data-testid="credit-card">
          <p className="flex items-center gap-2 text-sm text-muted">
            <Wallet className="size-4 text-primary" aria-hidden /> {t("credit")}
          </p>
          <p className="mt-1 text-3xl font-semibold text-strong" data-testid="credit-balance">
            {money(summary.creditBalance)}
          </p>
          <p className="text-sm text-muted">{t("creditHelp")}</p>
        </Card>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>{t("how.title")}</CardTitle>
        </CardHeader>
        <CardContent>
          <ul className="list-disc space-y-1 ps-5 text-sm text-muted" data-testid="rewards-rules">
            {example ? <li>{t("how.earn", { points: example.points, amount: money(example.perBdt) })}</li> : null}
            <li>{t("how.spend", { value: money(rules.pointValue), min: toNumber(rules.minPointsToRedeem) })}</li>
            <li>{t("how.cap", { percent: toNumber(rules.maxTenderPercent) })}</li>
            {toNumber(rules.pointsExpiryMonths) > 0 ? <li>{t("how.expiry", { months: toNumber(rules.pointsExpiryMonths) })}</li> : <li>{t("how.noExpiry")}</li>}
          </ul>
        </CardContent>
      </Card>

      <div className="grid gap-5 lg:grid-cols-2">
        <ReferralCard summary={summary} />
        <GiftCardRedeem />
      </div>

      <section aria-labelledby="rw-history" className="space-y-3">
        <h2 id="rw-history" className="text-lg font-semibold text-strong">
          {t("history")}
        </h2>
        <Tabs
          tabs={[
            { id: "points", label: t("tabs.points") },
            { id: "credit", label: t("tabs.credit") },
          ]}
          value={tab}
          onValueChange={setTab}
          label={t("history")}
        >
          {(active) =>
            active === "points" ? (
              <PointsLedgerTable data={points.data} loading={points.isLoading} page={pointsPage} onPageChange={setPointsPage} caption={t("tabs.points")} />
            ) : (
              <CreditLedgerTable data={credit.data} loading={credit.isLoading} page={creditPage} onPageChange={setCreditPage} caption={t("tabs.credit")} />
            )
          }
        </Tabs>
        {points.isError || credit.isError ? <Alert variant="warning">{t("loadFailed")}</Alert> : null}
      </section>
    </div>
  );
}

function ReferralCard({ summary }: { summary: RewardsSummary }) {
  const t = useTranslations("loyalty.me.referral");
  const money = useMoney();
  const referral = useMyReferral();
  return (
    <Card data-testid="referral-card">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Users className="size-5 text-primary" aria-hidden /> {t("title")}
        </CardTitle>
        <CardDescription>{t("help", { referrer: money(summary.rules.referralRewardReferrer), referee: money(summary.rules.referralRewardReferee) })}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {referral.isPending ? <Skeleton className="h-24" aria-busy /> : referral.isError ? <Alert variant="warning">{t("loadFailed")}</Alert> : <ReferralBody referral={referral.data} />}
      </CardContent>
    </Card>
  );
}

function ReferralBody({ referral }: { referral: MyReferral }) {
  const t = useTranslations("loyalty.me.referral");
  const money = useMoney();
  const [text, setText] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const apply = useApplyReferral();
  const errorText = useLoyaltyErrorText();
  const canShare = typeof navigator !== "undefined" && typeof navigator.share === "function";
  const alreadyReferred = !!referral.referredByName || !!referral.myStatus;

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(referral.code);
      toast.success(t("copied"));
    } catch {
      toast.error(t("copyFailed"));
    }
  };
  const share = async () => {
    try {
      await navigator.share({ title: t("shareTitle"), text: t("shareText", { code: referral.code }) });
    } catch {
      // The person closed the share sheet: nothing to report.
    }
  };
  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    if (!isValidReferralCode(text)) return setError(t("codeInvalid"));
    try {
      await apply.mutateAsync(normalizeCode(text));
      setText("");
      toast.success(t("applied"));
    } catch (failure) {
      setError(errorText(failure));
    }
  };

  return (
    <>
      <div className="space-y-2">
        <p className="text-sm text-muted">{t("yourCode")}</p>
        <p className="rounded-control border border-line bg-sunken p-3 text-center font-mono text-2xl font-semibold tracking-[0.2em] text-strong" data-testid="referral-code">
          {referral.code}
        </p>
        <div className="flex flex-wrap gap-2">
          <Button variant="secondary" onClick={() => void copy()} data-testid="referral-copy">
            <Copy aria-hidden /> {t("copy")}
          </Button>
          {canShare ? (
            <Button variant="secondary" onClick={() => void share()}>
              <Share2 aria-hidden /> {t("share")}
            </Button>
          ) : null}
        </div>
      </div>
      <dl className="grid grid-cols-3 gap-2 text-center text-sm" data-testid="referral-stats">
        <div className="rounded-control bg-sunken p-2">
          <dt className="text-muted">{t("rewarded")}</dt>
          <dd className="text-lg font-semibold text-strong">{toNumber(referral.rewarded)}</dd>
        </div>
        <div className="rounded-control bg-sunken p-2">
          <dt className="text-muted">{t("pending")}</dt>
          <dd className="text-lg font-semibold text-strong">{toNumber(referral.pending)}</dd>
        </div>
        <div className="rounded-control bg-sunken p-2">
          <dt className="text-muted">{t("earned")}</dt>
          <dd className="text-lg font-semibold text-strong">{money(referral.creditEarned)}</dd>
        </div>
      </dl>
      {toNumber(referral.maxRewards) > 0 ? <p className="text-sm text-muted">{t("limit", { max: toNumber(referral.maxRewards) })}</p> : null}

      <div className="border-t border-line pt-4">
        {alreadyReferred ? (
          <Alert variant="success" data-testid="referral-applied">
            {referral.referredByName ? t("referredBy", { name: referral.referredByName }) : t("referredAnon")}
            {referral.myStatus ? (
              <Badge variant="outline" className="ms-2">
                {t(`status.${referral.myStatus}`)}
              </Badge>
            ) : null}
          </Alert>
        ) : (
          <form onSubmit={(e) => void submit(e)} noValidate className="space-y-3" aria-label={t("applyTitle")}>
            <Field label={t("applyTitle")} htmlFor="rw-ref-code" hint={t("applyHint")} error={error ?? undefined}>
              <Input id="rw-ref-code" value={text} onChange={(e) => setText(e.target.value)} autoComplete="off" autoCapitalize="characters" maxLength={20} aria-invalid={!!error} className="font-mono uppercase" />
            </Field>
            <Button type="submit" loading={apply.isPending} data-testid="referral-apply">
              {t("apply")}
            </Button>
          </form>
        )}
      </div>
    </>
  );
}

function GiftCardRedeem() {
  const t = useTranslations("loyalty.me.gift");
  const money = useMoney();
  const redeem = useRedeemGiftCard();
  const errorText = useLoyaltyErrorText();
  const [text, setText] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [done, setDone] = React.useState<{ added: number; balance: number } | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    setDone(null);
    if (!isValidGiftCardCode(text)) return setError(t("codeInvalid"));
    try {
      const result = await redeem.mutateAsync(normalizeCode(text));
      setText("");
      setDone({ added: toNumber(result.added), balance: toNumber(result.creditBalance) });
    } catch (failure) {
      setError(errorText(failure));
    }
  };

  return (
    <Card data-testid="gift-redeem-card">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Ticket className="size-5 text-primary" aria-hidden /> {t("title")}
        </CardTitle>
        <CardDescription>{t("help")}</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={(e) => void submit(e)} noValidate className="space-y-3" aria-label={t("title")}>
          <Field label={t("code")} htmlFor="rw-gift-code" hint={t("hint")} error={error ?? undefined}>
            <Input
              id="rw-gift-code"
              value={text}
              onChange={(e) => setText(formatGiftCardCode(e.target.value))}
              autoComplete="off"
              autoCapitalize="characters"
              maxLength={19}
              aria-invalid={!!error}
              className="font-mono uppercase"
            />
          </Field>
          <Button type="submit" loading={redeem.isPending} data-testid="gift-redeem">
            {t("redeem")}
          </Button>
        </form>
        {done ? (
          <Alert variant="success" className="mt-3" data-testid="gift-redeemed">
            {t("done", { added: money(done.added), balance: money(done.balance) })}
          </Alert>
        ) : null}
      </CardContent>
    </Card>
  );
}
