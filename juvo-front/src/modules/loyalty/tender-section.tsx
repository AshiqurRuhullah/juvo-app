"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Gift, Sparkles, Ticket, Wallet } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { Section } from "@/modules/orders/common";
import type { CheckoutQuote } from "@/modules/orders/api";
import { useStoreSession, signInUrl } from "@/modules/store/store-session";
import { useUsable } from "./api";
import { errorKey } from "./common";
import {
  capSummary,
  clampPoints,
  creditRoom,
  emptyTenderDraft,
  formatGiftCardCode,
  hasSpendable,
  maxPointsToOffer,
  normalizeCode,
  parseAmount,
  parseWhole,
  payableAfterTender,
  pointsToAmount,
  tenderRows,
  toTenderRequest,
  toUsable,
  type TenderDraft,
  type TenderRequest,
} from "./logic";

/**
 * Checkout: pay part of the order with points, store credit or a gift card (FR-AB01, AB03). A tender never changes a price, a discount,
 * the tax or the total; it only reduces what is still to be paid. The server works out the plan: the amounts asked for go with the quote
 * (`onChange`), and what comes back in `quote.tender` is shown here. Not drawn at all while the rewards feature is off.
 */
export function TenderSection({ quote, onChange }: { quote: CheckoutQuote | undefined; onChange: (request: TenderRequest | null) => void }) {
  const plan = quote?.tender ?? null;
  const { customer } = useStoreSession();
  if (!quote || !plan || !plan.enabled) return null;
  return <TenderBody quote={quote} signedIn={!!customer} onChange={onChange} />;
}

function TenderBody({ quote, signedIn, onChange }: { quote: CheckoutQuote; signedIn: boolean; onChange: (request: TenderRequest | null) => void }) {
  const t = useTranslations("loyalty.tender");
  const te = useTranslations("errors");
  const money = useMoney();
  const plan = quote.tender!;
  const total = toNumber(quote.grandTotal);
  const usableQuery = useUsable(total, signedIn);
  const usable = toUsable(usableQuery.data);
  const [draft, setDraft] = React.useState<TenderDraft>(emptyTenderDraft);
  const [applied, setApplied] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);

  const spendable = signedIn && hasSpendable(plan);
  const rows = tenderRows(plan);
  const tenderTotal = toNumber(plan.tenderTotal);
  const left = payableAfterTender(total, tenderTotal);
  const set = <K extends keyof TenderDraft>(key: K, value: TenderDraft[K]) => setDraft((old) => ({ ...old, [key]: value }));

  const maxPoints = usable ? maxPointsToOffer(usable) : 0;
  const pointsNow = parseWhole(draft.points);
  const maxCredit = usable ? creditRoom(usable, usable ? clampPoints(pointsNow, usable) : 0) : 0;

  const apply = () => {
    setNotice(null);
    let points = parseWhole(draft.points);
    let credit = parseAmount(draft.credit);
    if (usable && signedIn) {
      const safePoints = clampPoints(points, usable);
      if (safePoints !== points) setNotice(points > 0 && safePoints === 0 ? t("pointsBelowMin", { min: usable.minPointsToRedeem }) : t("adjusted"));
      points = safePoints;
      const room = creditRoom(usable, points);
      if (credit > room) {
        credit = room;
        setNotice(t("adjusted"));
      }
    } else {
      points = 0;
      credit = 0;
    }
    const next: TenderDraft = { points: points ? String(points) : "", credit: credit ? String(credit) : "", giftCardCode: draft.giftCardCode };
    setDraft(next);
    const request = toTenderRequest(next);
    setApplied(request !== null);
    onChange(request);
  };

  const remove = () => {
    setDraft(emptyTenderDraft);
    setApplied(false);
    setNotice(null);
    onChange(null);
  };

  const problemText = plan.problem ? (te.has(errorKey(plan.problem)) ? te(errorKey(plan.problem)) : (plan.problemMessage ?? te("GENERAL_BAD_REQUEST"))) : null;
  const cap = usable ? capSummary(usable) : null;

  return (
    <Section title={t("title")} id="co-tender">
      <div className="space-y-4" data-testid="tender-section">
        <p className="text-sm text-muted">{t("help")}</p>

        {signedIn ? (
          spendable ? (
            <p className="flex flex-wrap gap-x-6 gap-y-1 text-sm text-strong" data-testid="tender-balances">
              <span className="inline-flex items-center gap-1.5">
                <Sparkles className="size-4 text-primary" aria-hidden /> {t("pointsBalance", { points: toNumber(plan.pointsBalance), value: usable ? money(pointsToAmount(toNumber(plan.pointsBalance), usable.pointValue)) : "" })}
              </span>
              <span className="inline-flex items-center gap-1.5">
                <Wallet className="size-4 text-primary" aria-hidden /> {t("creditBalance", { amount: money(plan.creditBalance) })}
              </span>
            </p>
          ) : (
            <p className="text-sm text-muted" data-testid="tender-none">
              {t("noBalance")}
            </p>
          )
        ) : null}

        {spendable ? (
          <div className="grid gap-4 sm:grid-cols-2">
            {toNumber(plan.pointsBalance) > 0 ? (
              <Field label={t("points")} htmlFor="td-points" hint={usable ? (maxPoints > 0 ? t("pointsHint", { min: usable.minPointsToRedeem, value: money(usable.pointValue) }) : t("pointsUnavailable", { min: usable.minPointsToRedeem })) : undefined}>
                <div className="flex gap-2">
                  <Input id="td-points" inputMode="numeric" value={draft.points} onChange={(e) => set("points", e.target.value)} autoComplete="off" data-testid="tender-points" />
                  {maxPoints > 0 ? (
                    <Button type="button" variant="secondary" onClick={() => set("points", String(maxPoints))} data-testid="tender-points-max">
                      {t("useMax")}
                    </Button>
                  ) : null}
                </div>
              </Field>
            ) : null}
            {toNumber(plan.creditBalance) > 0 ? (
              <Field label={t("credit")} htmlFor="td-credit" hint={usable ? t("creditHint", { max: money(maxCredit) }) : undefined}>
                <div className="flex gap-2">
                  <Input id="td-credit" inputMode="decimal" value={draft.credit} onChange={(e) => set("credit", e.target.value)} autoComplete="off" data-testid="tender-credit" />
                  {maxCredit > 0 ? (
                    <Button type="button" variant="secondary" onClick={() => set("credit", String(maxCredit))} data-testid="tender-credit-max">
                      {t("useMax")}
                    </Button>
                  ) : null}
                </div>
              </Field>
            ) : null}
          </div>
        ) : null}

        {cap && spendable ? (
          <p className="text-sm text-muted" data-testid="tender-cap">
            {t("cap", { percent: cap.percent, amount: money(cap.amount) })}
          </p>
        ) : null}

        <Field label={t("giftCard")} htmlFor="td-gift" hint={t("giftHint")}>
          <div className="relative">
            <Ticket className="pointer-events-none absolute start-3.5 top-1/2 size-5 -translate-y-1/2 text-muted" aria-hidden />
            <Input id="td-gift" value={draft.giftCardCode} onChange={(e) => set("giftCardCode", formatGiftCardCode(e.target.value))} autoComplete="off" autoCapitalize="characters" maxLength={19} className="ps-11 font-mono uppercase" data-testid="tender-gift" />
          </div>
        </Field>

        {!signedIn ? (
          <p className="text-sm text-muted">
            {t("guestNote")}{" "}
            <Link href={signInUrl("/checkout")} className="inline-flex min-h-11 min-w-11 items-center justify-center font-semibold text-primary underline-offset-4 hover:underline">
              {t("signIn")}
            </Link>
          </p>
        ) : null}

        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="secondary" onClick={apply} disabled={!draft.points && !draft.credit && normalizeCode(draft.giftCardCode) === ""} data-testid="tender-apply">
            <Gift aria-hidden /> {t("apply")}
          </Button>
          {applied ? (
            <Button type="button" variant="ghost" onClick={remove} data-testid="tender-remove">
              {t("remove")}
            </Button>
          ) : null}
        </div>

        {notice ? <p className="text-sm text-muted">{notice}</p> : null}
        {problemText ? (
          <Alert variant="danger" data-testid="tender-problem">
            {problemText}
          </Alert>
        ) : null}

        {rows.length > 0 && !plan.problem ? (
          <dl className="space-y-2 border-t border-line pt-4 text-sm" data-testid="tender-plan" aria-live="polite">
            {rows.map((r) => (
              <div key={r.key} className="flex justify-between gap-3">
                <dt className="text-muted">{r.key === "points" ? t("rowPoints", { points: r.points ?? 0 }) : r.key === "credit" ? t("rowCredit") : t("rowGift")}</dt>
                <dd className="text-end text-success-ink" data-testid={`tender-row-${r.key}`}>
                  -{money(r.amount)}
                </dd>
              </div>
            ))}
            <div className="flex justify-between gap-3 border-t border-line pt-2 text-base font-semibold">
              <dt className="text-strong">{t("left")}</dt>
              <dd className="text-strong" data-testid="tender-left">
                {money(left)}
              </dd>
            </div>
          </dl>
        ) : null}
        {rows.length > 0 && !plan.problem ? <p className="text-sm text-muted">{t("totalUnchanged", { total: money(total) })}</p> : null}
      </div>
    </Section>
  );
}
