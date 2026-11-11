"use client";

import * as React from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { CalendarClock, PartyPopper, Truck } from "lucide-react";
import { Field, Label, RadioGroup, RadioItem } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { useCountries, useDistricts, useDivisions, useUpazilas, type DeliveryQuote } from "@/modules/store/api";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { readSavedPlace, useCartQuote, writeSavedPlace, type SavedPlace } from "./hooks";
import { useCartErrorText } from "./use-cart-error";

const NONE = "__none";

/** "name in Bengali" when the page is Bengali and the name exists, else the English name. */
function useLabel() {
  const locale = useLocale();
  return (en: string, bn: string | null | undefined) => (locale === "bn" && bn ? bn : en);
}

/**
 * Delivery estimate for the cart: Bangladesh by division, district and (optionally) upazila, or another enabled country. The charge
 * is worked out by the API from the zone, weight and order value, and the amount shown here is the amount that will be billed (BR-G2).
 * The last place chosen is remembered in the browser. This component mounts after the cart has loaded, so reading storage in the
 * initial state is safe (no server markup to disagree with).
 */
export function DeliveryEstimate({ cartKey, subtotal, onQuote }: { cartKey: string; subtotal: number; onQuote: (quote: DeliveryQuote | undefined) => void }) {
  const t = useTranslations("cart.delivery");
  const money = useMoney();
  const label = useLabel();
  const format = useFormatter();
  const errorText = useCartErrorText();

  const [place, setPlace] = React.useState<SavedPlace>(() => readSavedPlace() ?? { mode: "bd" });
  const update = (next: SavedPlace) => {
    setPlace(next);
    writeSavedPlace(next);
  };

  const divisions = useDivisions();
  const districts = useDistricts(place.mode === "bd" ? place.divisionId : undefined);
  const upazilas = useUpazilas(place.mode === "bd" ? place.districtId : undefined);
  const countries = useCountries();

  const target = place.mode === "bd" ? { districtId: place.districtId, upazilaId: place.upazilaId } : { countryCode: place.countryCode };
  const quote = useCartQuote(target, cartKey);
  const result = quote.data;
  const reported = React.useRef<DeliveryQuote | undefined>(undefined);
  // Tell the cart page the current charge (used for the grand total) once per change, from an event-like callback.
  React.useEffect(() => {
    if (reported.current !== result) {
      reported.current = result;
      onQuote(result);
    }
  }, [result, onQuote]);

  const foreign = (countries.data ?? []).filter((c) => c.isEnabled && c.code !== "BD");
  const free = result && toNumber(result.amountToFreeDelivery) > 0 ? toNumber(result.amountToFreeDelivery) : 0;
  const progress = result && free > 0 ? Math.min(99, Math.round((subtotal / (subtotal + free)) * 100)) : 100;

  return (
    <section aria-labelledby="delivery-title" className="space-y-4">
      <div>
        <h2 id="delivery-title" className="flex items-center gap-2 text-lg font-semibold text-strong">
          <Truck className="size-5 text-primary" aria-hidden /> {t("title")}
        </h2>
        <p className="text-sm text-muted">{t("hint")}</p>
      </div>

      {foreign.length > 0 ? (
        <RadioGroup value={place.mode} onValueChange={(v) => update(v === "abroad" ? { mode: "abroad", countryCode: place.countryCode } : { mode: "bd", divisionId: place.divisionId, districtId: place.districtId, upazilaId: place.upazilaId })} className="grid grid-cols-2 gap-2" aria-label={t("title")}>
          {(["bd", "abroad"] as const).map((mode) => (
            <div key={mode} className="flex min-h-12 items-center gap-3 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
              <RadioItem id={`mode-${mode}`} value={mode} />
              <Label htmlFor={`mode-${mode}`} className="flex min-h-11 flex-1 cursor-pointer items-center">
                {mode === "bd" ? t("bangladesh") : t("abroad")}
              </Label>
            </div>
          ))}
        </RadioGroup>
      ) : null}

      {place.mode === "bd" ? (
        <div className="grid gap-3 sm:grid-cols-3 lg:grid-cols-1 xl:grid-cols-3">
          <Field label={t("division")} htmlFor="del-division">
            <Select
              id="del-division"
              value={place.divisionId ?? ""}
              onValueChange={(v) => update({ mode: "bd", divisionId: v })}
              options={(divisions.data ?? []).map((d) => ({ value: d.id, label: label(d.nameEn, d.nameBn) }))}
              placeholder={t("choose")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
            />
          </Field>
          <Field label={t("district")} htmlFor="del-district">
            <Select
              id="del-district"
              value={place.districtId ?? ""}
              onValueChange={(v) => update({ mode: "bd", divisionId: place.divisionId, districtId: v })}
              options={(districts.data ?? []).map((d) => ({ value: d.id, label: label(d.nameEn, d.nameBn) }))}
              placeholder={t("choose")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              disabled={!place.divisionId}
            />
          </Field>
          <Field label={t("upazila")} htmlFor="del-upazila">
            <Select
              id="del-upazila"
              value={place.upazilaId ?? NONE}
              onValueChange={(v) => update({ mode: "bd", divisionId: place.divisionId, districtId: place.districtId, upazilaId: v === NONE ? undefined : v })}
              options={[{ value: NONE, label: t("anyUpazila") }, ...(upazilas.data ?? []).map((u) => ({ value: u.id, label: label(u.nameEn, u.nameBn) }))]}
              placeholder={t("choose")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              disabled={!place.districtId}
            />
          </Field>
        </div>
      ) : (
        <Field label={t("country")} htmlFor="del-country">
          <Select
            id="del-country"
            value={place.countryCode ?? ""}
            onValueChange={(v) => update({ mode: "abroad", countryCode: v })}
            options={foreign.map((c) => ({ value: c.code, label: label(c.nameEn, c.nameBn) }))}
            placeholder={t("choose")}
            searchPlaceholder={t("search")}
            emptyText={t("noMatch")}
          />
        </Field>
      )}

      <div aria-live="polite" className="space-y-3">
        {quote.isFetching && !result ? <Skeleton className="h-24" /> : null}
        {quote.isError ? <Alert variant="warning">{errorText(quote.error)}</Alert> : null}
        {result ? (
          <div className="space-y-3 rounded-control border border-line bg-sunken/60 p-4" data-testid="delivery-quote">
            <p className="text-sm text-muted">{t("zone", { zone: label(result.zoneNameEn, result.zoneNameBn) })}</p>
            <dl className="space-y-1.5 text-sm">
              <div className="flex justify-between gap-3">
                <dt className="text-muted">{t("base")}</dt>
                <dd className="text-strong">{money(result.baseFee)}</dd>
              </div>
              {toNumber(result.weightFee) > 0 ? (
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("weight")}</dt>
                  <dd className="text-strong">{money(result.weightFee)}</dd>
                </div>
              ) : null}
              {toNumber(result.remoteSurcharge) > 0 ? (
                <div className="flex justify-between gap-3">
                  <dt className="text-muted">{t("remote")}</dt>
                  <dd className="text-strong">{money(result.remoteSurcharge)}</dd>
                </div>
              ) : null}
              {toNumber(result.freeDeliveryWaiver) > 0 ? (
                <div className="flex justify-between gap-3 text-success-ink">
                  <dt>{t("waiver")}</dt>
                  <dd>-{money(result.freeDeliveryWaiver)}</dd>
                </div>
              ) : null}
              <div className="flex justify-between gap-3 border-t border-line pt-2 text-base font-semibold">
                <dt className="text-strong">{t("fee")}</dt>
                <dd className="text-strong" data-testid="delivery-fee">
                  {result.isFree ? t("freeShort") : money(result.total)}
                </dd>
              </div>
            </dl>
            {result.isFree ? (
              <p className="flex items-center gap-2 rounded-control bg-success-soft p-2.5 text-sm font-medium text-success-ink" data-testid="free-delivery">
                <PartyPopper className="size-5 shrink-0" aria-hidden /> {t("free")}
              </p>
            ) : free > 0 ? (
              <div className="space-y-1.5" data-testid="free-progress">
                <p className="text-sm text-strong">{t("more", { amount: money(free) })}</p>
                <div role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress} aria-label={t("progress")} className="h-2 overflow-hidden rounded-full bg-line">
                  <div className="h-full rounded-full bg-primary transition-[width] duration-300" style={{ width: `${progress}%` }} />
                </div>
              </div>
            ) : null}
            <p className="flex items-start gap-2 text-sm text-muted">
              <CalendarClock className="mt-0.5 size-4 shrink-0" aria-hidden />
              <span>
                {t("days", { min: toNumber(result.daysMin), max: toNumber(result.daysMax) })} {t("earliest", { date: format.dateTime(new Date(result.earliestDelivery), { dateStyle: "medium" }) })}
              </span>
            </p>
          </div>
        ) : !quote.isFetching && !quote.isError ? (
          <p className="text-sm text-muted">{t("pickPlace")}</p>
        ) : null}
      </div>
    </section>
  );
}
