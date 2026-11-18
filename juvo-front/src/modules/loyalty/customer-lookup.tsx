"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Ban, CircleCheck, PencilLine, Search, UserRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Tabs } from "@/components/ui/tabs";
import { Alert, Badge, Card, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PAGE_SIZE } from "@/lib/pagination";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { newKey } from "@/modules/orders/status";
import { useAdjustRewards, useCustomerCredit, useCustomerPoints, useCustomerRewards, useCustomerSearch, type CustomerListItem } from "./api";
import { CreditLedgerTable, PointsLedgerTable, problemCodeOf, useLoyaltyErrorText, useLoyaltyPermissions } from "./common";
import { DialogForm } from "./dialog-form";
import { validateAdjust, type AdjustDraft } from "./logic";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Staff `Customer rewards` tab: find a customer, see both balances and ledgers, add or take points and store credit with a reason. */
export default function CustomerLookup() {
  const t = useTranslations("loyalty.lookup");
  const perms = useLoyaltyPermissions();
  const [text, setText] = React.useState("");
  const [customerId, setCustomerId] = React.useState<string | null>(null);
  const [picked, setPicked] = React.useState<CustomerListItem | null>(null);
  const typedId = UUID.test(text.trim()) ? text.trim() : null;
  const search = useCustomerSearch(typedId ? "" : text, perms.customerView);

  const choose = (customer: CustomerListItem) => {
    setPicked(customer);
    setCustomerId(customer.id);
  };

  return (
    <div className="space-y-5">
      <form
        className="space-y-3"
        onSubmit={(e) => {
          e.preventDefault();
          if (typedId) {
            setPicked(null);
            setCustomerId(typedId);
          }
        }}
        role="search"
        aria-label={t("title")}
      >
        <Field label={t("search")} htmlFor="lk-search" hint={perms.customerView ? t("searchHint") : t("searchHintId")}>
          <div className="relative">
            <Search className="pointer-events-none absolute start-3.5 top-1/2 size-5 -translate-y-1/2 text-muted" aria-hidden />
            <Input id="lk-search" value={text} onChange={(e) => setText(e.target.value)} className="ps-11" autoComplete="off" data-testid="lookup-search" />
          </div>
        </Field>
        {typedId ? (
          <Button type="submit" variant="secondary" data-testid="lookup-open-id">
            {t("openId")}
          </Button>
        ) : null}
      </form>
      {!perms.customerView ? <Alert variant="info">{t("noSearchPermission")}</Alert> : null}

      {perms.customerView && !typedId && text.trim().length >= 3 ? (
        search.isError ? (
          <Alert variant="danger">{t("searchFailed")}</Alert>
        ) : (search.data?.items.length ?? 0) === 0 && !search.isFetching ? (
          <p className="text-sm text-muted" data-testid="lookup-none">
            {t("none")}
          </p>
        ) : (
          <ul className="divide-y divide-line rounded-card border border-line bg-card" aria-label={t("results")} data-testid="lookup-results">
            {(search.data?.items ?? []).map((c) => (
              <li key={c.id}>
                <button type="button" onClick={() => choose(c)} className="flex min-h-11 w-full items-center gap-3 px-4 py-2 text-start hover:bg-sunken" data-testid="lookup-result">
                  <UserRound className="size-5 shrink-0 text-primary" aria-hidden />
                  <span className="min-w-0">
                    <span className="block font-medium text-strong">{c.fullName ?? t("unnamed")}</span>
                    <span className="block text-sm text-muted">{c.mobile}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )
      ) : null}

      {customerId ? <CustomerPanel key={customerId} customerId={customerId} fallbackName={picked?.fullName ?? null} /> : null}
    </div>
  );
}

function CustomerPanel({ customerId, fallbackName }: { customerId: string; fallbackName: string | null }) {
  const t = useTranslations("loyalty.lookup");
  const tl = useTranslations("loyalty.ledger");
  const money = useMoney();
  const perms = useLoyaltyPermissions();
  const errorText = useLoyaltyErrorText();
  const summary = useCustomerRewards(customerId);
  const [tab, setTab] = React.useState("points");
  const [pointsPage, setPointsPage] = React.useState(1);
  const [creditPage, setCreditPage] = React.useState(1);
  const [adjusting, setAdjusting] = React.useState(false);
  const points = useCustomerPoints(customerId, pointsPage, PAGE_SIZE);
  const credit = useCustomerCredit(customerId, creditPage, PAGE_SIZE);

  if (summary.isPending) return <Skeleton className="h-40" aria-busy />;
  if (summary.isError || !summary.data) {
    return (
      <Alert variant="danger" data-testid="lookup-error">
        {problemCodeOf(summary.error) === "LOYALTY.CUSTOMER_NOT_FOUND" ? t("notFound") : errorText(summary.error)}
      </Alert>
    );
  }
  const s = summary.data;
  return (
    <div className="space-y-4" data-testid="customer-rewards">
      <Card className="space-y-3 p-4 sm:p-5">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h2 className="text-lg font-semibold text-strong">{s.name ?? fallbackName ?? t("unnamed")}</h2>
            <p className="text-sm text-muted">{s.mobile}</p>
          </div>
          {perms.pointsAdjust ? (
            <Button variant="secondary" onClick={() => setAdjusting(true)} data-testid="adjust-open">
              <PencilLine aria-hidden /> {t("adjust.open")}
            </Button>
          ) : null}
        </div>
        <dl className="grid gap-3 sm:grid-cols-3">
          <div className="rounded-control bg-sunken p-3">
            <dt className="text-sm text-muted">{t("points")}</dt>
            <dd className="text-2xl font-semibold text-strong" data-testid="lookup-points">
              {toNumber(s.pointsBalance)}
            </dd>
            {toNumber(s.pointsExpiringSoon) > 0 ? <dd className="text-sm text-muted">{t("expiringSoon", { points: toNumber(s.pointsExpiringSoon) })}</dd> : null}
          </div>
          <div className="rounded-control bg-sunken p-3">
            <dt className="text-sm text-muted">{t("credit")}</dt>
            <dd className="text-2xl font-semibold text-strong" data-testid="lookup-credit">
              {money(s.creditBalance)}
            </dd>
          </div>
          <div className="rounded-control bg-sunken p-3">
            <dt className="text-sm text-muted">{t("consistency")}</dt>
            <dd>
              {s.ledgerConsistent ? (
                <Badge variant="success" data-testid="ledger-consistent">
                  <CircleCheck className="size-3.5" aria-hidden /> {t("consistent")}
                </Badge>
              ) : (
                <Badge variant="danger" data-testid="ledger-inconsistent">
                  <Ban className="size-3.5" aria-hidden /> {t("inconsistent")}
                </Badge>
              )}
            </dd>
            <dd className="mt-1 text-sm text-muted">{t("sums", { points: toNumber(s.pointsLedgerSum), credit: money(s.creditLedgerSum) })}</dd>
          </div>
        </dl>
      </Card>

      <Tabs
        tabs={[
          { id: "points", label: tl("tabs.points") },
          { id: "credit", label: tl("tabs.credit") },
        ]}
        value={tab}
        onValueChange={setTab}
        label={tl("title")}
      >
        {(active) =>
          active === "points" ? (
            <PointsLedgerTable data={points.data} loading={points.isLoading} page={pointsPage} onPageChange={setPointsPage} caption={tl("tabs.points")} />
          ) : (
            <CreditLedgerTable data={credit.data} loading={credit.isLoading} page={creditPage} onPageChange={setCreditPage} caption={tl("tabs.credit")} />
          )
        }
      </Tabs>

      {adjusting ? <AdjustDialog customerId={customerId} onClose={() => setAdjusting(false)} /> : null}
    </div>
  );
}

function AdjustDialog({ customerId, onClose }: { customerId: string; onClose: () => void }) {
  const t = useTranslations("loyalty.lookup.adjust");
  const tc = useTranslations("loyalty.common");
  const adjust = useAdjustRewards();
  const errorText = useLoyaltyErrorText();
  const [key] = React.useState(() => newKey());
  const [draft, setDraft] = React.useState<AdjustDraft>({ points: "", credit: "", reason: "" });
  const [shown, setShown] = React.useState(false);
  const [failure, setFailure] = React.useState<string | null>(null);
  const errors = validateAdjust(draft);
  const set = <K extends keyof AdjustDraft>(k: K, v: AdjustDraft[K]) => setDraft((old) => ({ ...old, [k]: v }));

  const submit = async () => {
    setShown(true);
    setFailure(null);
    if (Object.keys(errors).length > 0) return;
    try {
      await adjust.mutateAsync({ customerId, draft, key });
      toast.success(t("done"));
      onClose();
    } catch (error) {
      setFailure(errorText(error));
    }
  };

  return (
    <DialogForm open onOpenChange={(o) => !o && onClose()} title={t("title")} description={t("text")} submitLabel={t("submit")} cancelLabel={tc("cancel")} closeLabel={tc("close")} loading={adjust.isPending} error={failure} onSubmit={() => void submit()}>
      <Field label={t("points")} htmlFor="adj-points" hint={t("signHint")} error={shown && errors.points ? t(`errors.${errors.points}`) : undefined}>
        <Input id="adj-points" inputMode="numeric" value={draft.points} onChange={(e) => set("points", e.target.value)} aria-invalid={!!(shown && errors.points)} autoComplete="off" data-testid="adjust-points" />
      </Field>
      <Field label={t("credit")} htmlFor="adj-credit" hint={t("signHint")} error={shown && errors.credit ? t(`errors.${errors.credit}`) : undefined}>
        <Input id="adj-credit" inputMode="decimal" value={draft.credit} onChange={(e) => set("credit", e.target.value)} aria-invalid={!!(shown && errors.credit)} autoComplete="off" data-testid="adjust-credit" />
      </Field>
      {shown && errors.none ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {t("errors.none")}
        </p>
      ) : null}
      <Field label={t("reason")} htmlFor="adj-reason" required hint={t("reasonHint")} error={shown && errors.reason ? t("errors.required") : undefined}>
        <Textarea id="adj-reason" rows={3} maxLength={300} value={draft.reason} onChange={(e) => set("reason", e.target.value)} aria-invalid={!!(shown && errors.reason)} data-testid="adjust-reason" />
      </Field>
    </DialogForm>
  );
}
