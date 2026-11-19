"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleCheck, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCountries, useDistricts, useQuoteTry, useUpazilas, type Quote, type Zone } from "./api";
import { useLocalName } from "./common";
import {
  expectedCharge, quoteAddsUp, quoteRows, quoteToRequest, ratesToForm, ratesToRequest, validateQuote,
  type QuoteErrorKey, type QuoteForm,
} from "./logic";

/**
 * "Try it": asks the same public endpoint the shop uses for a delivery charge (district, optional upazila, order value, weight) and shows
 * the numbers a customer would be shown. It reads the SAVED rates, so unsaved edits are flagged. The quote also includes a flat courier
 * charge when the first switched-on courier has one and a running free-delivery promotion; both are real parts of what customers pay.
 */
export function QuotePanel({ zone, unsaved, canViewCountries }: { zone: Zone; unsaved: boolean; canViewCountries: boolean }) {
  const t = useTranslations("delivery.quote");
  const te = useTranslations("delivery.quote.errors");
  const tc = useTranslations("delivery.common");
  const money = useMoney();
  const localName = useLocalName();
  const problemMessage = useProblemMessage();
  const districts = useDistricts();
  const countries = useCountries(zone.kind === "International" && canViewCountries);
  const run = useQuoteTry();
  const domestic = zone.kind === "Domestic";
  const firstDistrict = zone.areas[0]?.districtId ?? "";
  const [form, setForm] = React.useState<QuoteForm>({ countryCode: "", districtId: firstDistrict, upazilaId: "", orderValue: "1000", weightKg: "1" });
  const [errors, setErrors] = React.useState<Partial<Record<"districtId" | "countryCode" | "orderValue" | "weightKg", QuoteErrorKey>>>({});
  const [result, setResult] = React.useState<{ quote: Quote; request: ReturnType<typeof quoteToRequest> } | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);
  const upazilas = useUpazilas(domestic && form.districtId ? form.districtId : null);
  const set = <K extends keyof QuoteForm>(key: K, value: QuoteForm[K]) => {
    setForm((old) => ({ ...old, [key]: value, ...(key === "districtId" ? { upazilaId: "" } : {}) }));
    setResult(null);
  };
  const err = (key: keyof typeof errors) => (errors[key] ? te(errors[key]!) : undefined);
  const zoneCountries = (countries.data ?? []).filter((c) => c.zoneId === zone.id && c.isEnabled);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    setResult(null);
    const found = validateQuote(form, domestic);
    setErrors(found);
    if (Object.keys(found).length) return;
    const request = quoteToRequest(form, domestic);
    try {
      setResult({ quote: await run.mutateAsync(request), request });
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <div className="space-y-4" data-testid="quote-panel">
      <Alert variant="info">{t("howItWorks")}</Alert>
      {unsaved ? (
        <Alert variant="warning" data-testid="quote-unsaved">
          {t("unsaved")}
        </Alert>
      ) : null}
      <form onSubmit={(e) => void submit(e)} noValidate>
        <Card>
          <CardHeader>
            <CardTitle>{t("title")}</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="grid gap-4 sm:grid-cols-2">
              {domestic ? (
                <>
                  <Field label={t("district")} htmlFor="qt-district" required hint={zone.areas.length === 0 && !zone.isFallback ? t("noAreasHint") : undefined} error={err("districtId")}>
                    <Select
                      id="qt-district"
                      value={form.districtId}
                      onValueChange={(v) => set("districtId", v)}
                      options={(districts.data ?? []).map((d) => ({ value: d.id, label: localName(d) }))}
                      placeholder={t("chooseDistrict")}
                      searchPlaceholder={tc("search")}
                      emptyText={tc("noMatch")}
                      invalid={!!errors.districtId}
                      searchable
                    />
                  </Field>
                  <Field label={t("upazila")} htmlFor="qt-upazila" hint={t("upazilaHint")}>
                    <Select
                      id="qt-upazila"
                      value={form.upazilaId || "__none"}
                      onValueChange={(v) => set("upazilaId", v === "__none" ? "" : v)}
                      options={[{ value: "__none", label: t("noUpazila") }, ...(upazilas.data ?? []).map((u) => ({ value: u.id, label: localName(u) }))]}
                      placeholder={t("upazila")}
                      searchPlaceholder={tc("search")}
                      emptyText={tc("noMatch")}
                      disabled={!form.districtId}
                    />
                  </Field>
                </>
              ) : (
                <Field label={t("country")} htmlFor="qt-country" required hint={zoneCountries.length === 0 ? t("noCountriesHint") : undefined} error={err("countryCode")}>
                  <Select
                    id="qt-country"
                    value={form.countryCode}
                    onValueChange={(v) => set("countryCode", v)}
                    options={zoneCountries.map((c) => ({ value: c.code, label: localName(c) }))}
                    placeholder={t("chooseCountry")}
                    searchPlaceholder={tc("search")}
                    emptyText={tc("noMatch")}
                    invalid={!!errors.countryCode}
                  />
                </Field>
              )}
              <Field label={t("orderValue")} htmlFor="qt-value" hint={t("orderValueHint")} error={err("orderValue")}>
                <Input id="qt-value" inputMode="decimal" value={form.orderValue} onChange={(e) => set("orderValue", e.target.value)} aria-invalid={!!errors.orderValue} autoComplete="off" />
              </Field>
              <Field label={t("weight")} htmlFor="qt-weight" hint={t("weightHint")} error={err("weightKg")}>
                <Input id="qt-weight" inputMode="decimal" value={form.weightKg} onChange={(e) => set("weightKg", e.target.value)} aria-invalid={!!errors.weightKg} autoComplete="off" />
              </Field>
            </div>
            <Button type="submit" loading={run.isPending} data-testid="quote-run">
              {t("run")}
            </Button>
          </CardContent>
        </Card>
      </form>

      {failure ? (
        <Alert variant="danger" data-testid="quote-error">
          {failure}
        </Alert>
      ) : null}

      {result ? <QuoteResult zone={zone} quote={result.quote} request={result.request} money={money} /> : null}
    </div>
  );
}

function QuoteResult({ zone, quote, request, money }: { zone: Zone; quote: Quote; request: ReturnType<typeof quoteToRequest>; money: (v: number) => string }) {
  const t = useTranslations("delivery.quote");
  const tr = useTranslations("delivery.quote.rows");
  const rows = quoteRows(quote);
  const otherZone = quote.zoneCode !== zone.code;
  const expected = expectedCharge(ratesToRequest(ratesToForm(zone)), toNumber(quote.subtotal), toNumber(quote.weightGrams), toNumber(quote.remoteSurcharge) > 0);
  const matches = !otherZone && expected !== null && Math.abs(expected.total - toNumber(quote.total)) < 0.005;
  const label = (id: string) => (id === "waiver" ? tr("waiver") : tr(id as "base" | "weight" | "remote" | "total"));
  return (
    <Card data-testid="quote-result">
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2">
          {t("resultTitle")}
          <Badge variant="info" data-testid="quote-zone">
            {quote.zoneNameEn}
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {otherZone ? (
          <Alert variant="warning" data-testid="quote-other-zone">
            {t("otherZone", { zone: quote.zoneNameEn, code: quote.zoneCode })}
          </Alert>
        ) : null}
        <dl className="divide-y divide-line rounded-card border border-line">
          {rows.map((r) => (
            <div key={r.id} className={`flex items-center justify-between gap-3 px-4 py-3 ${r.id === "total" ? "bg-sunken font-semibold text-strong" : ""}`} data-testid={`quote-${r.id}`} data-amount={r.amount}>
              <dt>{label(r.id)}</dt>
              <dd className="tabular-nums">{r.amount < 0 ? `-${money(-r.amount)}` : money(r.amount)}</dd>
            </div>
          ))}
        </dl>
        <ul className="space-y-1 text-sm text-muted">
          <li data-testid="quote-days">{t("days", { min: toNumber(quote.daysMin), max: toNumber(quote.daysMax), from: quote.earliestDelivery, to: quote.latestDelivery })}</li>
          {quote.isFree ? <li>{t("isFree")}</li> : quote.amountToFreeDelivery !== null ? <li data-testid="quote-to-free">{t("toFree", { amount: money(toNumber(quote.amountToFreeDelivery)) })}</li> : <li>{t("noFree")}</li>}
          {quote.dutiesPaidByReceiver ? <li>{t("duties")}</li> : null}
          {quote.promotionLabelEn ? <li data-testid="quote-promo">{t("promotion", { label: quote.promotionLabelEn })}</li> : null}
          <li>{t("inputs", { value: money(request.subtotal), weight: request.weightGrams / 1000 })}</li>
        </ul>
        <p className="flex items-start gap-2 text-sm" data-testid="quote-check">
          {matches && quoteAddsUp(quote) ? <CircleCheck className="mt-0.5 size-4 shrink-0 text-success" aria-hidden /> : <TriangleAlert className="mt-0.5 size-4 shrink-0 text-warning-ink" aria-hidden />}
          <span>{matches && quoteAddsUp(quote) ? t("matches") : t("differs")}</span>
        </p>
      </CardContent>
    </Card>
  );
}
