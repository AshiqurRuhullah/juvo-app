"use client";

import * as React from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { Truck } from "lucide-react";
import { useMutation } from "@tanstack/react-query";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { api, unwrap } from "@/lib/api/client";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useDistricts, useDivisions, useUpazilas, type DeliveryQuote } from "./api";
import { useStoreError } from "./hooks";

/**
 * "Delivery to…" on the product page: pick a division and district (upazila is optional) and the API works out the charge and the
 * earliest delivery date for this item, so the customer knows before adding it to the cart. Bangladesh only here; the cart offers
 * the other enabled countries.
 */
export function ProductDelivery({ variantId, price, weightGrams }: { variantId?: string; price: number; weightGrams: number }) {
  const t = useTranslations("store.delivery");
  const locale = useLocale();
  const money = useMoney();
  const format = useFormatter();
  const storeError = useStoreError();
  const [divisionId, setDivisionId] = React.useState("");
  const [districtId, setDistrictId] = React.useState("");
  const [upazilaId, setUpazilaId] = React.useState("");
  const divisions = useDivisions();
  const districts = useDistricts(divisionId);
  const upazilas = useUpazilas(districtId);
  const label = (en: string, bn: string | null | undefined) => (locale === "bn" && bn ? bn : en);

  const quote = useMutation({
    mutationFn: async (): Promise<DeliveryQuote> =>
      unwrap(
        await api.POST("/api/v1/store/delivery/quote", {
          body: { countryCode: "BD", districtId, upazilaId: upazilaId || null, items: variantId ? [{ variantId, quantity: 1 }] : [], subtotal: price, weightGrams },
        }),
      ) as DeliveryQuote,
  });

  const run = quote.mutate;
  React.useEffect(() => {
    if (districtId) run();
  }, [districtId, upazilaId, variantId, run]);

  const result = districtId ? quote.data : undefined;

  return (
    <section className="space-y-3 rounded-card border border-line bg-card p-4" aria-labelledby="delivery-heading">
      <h2 id="delivery-heading" className="flex items-center gap-2 font-sans text-base font-semibold">
        <Truck className="size-5 text-primary" aria-hidden /> {t("title")}
      </h2>
      <div className="grid gap-2 sm:grid-cols-2">
        <Select
          aria-label={t("division")}
          placeholder={t("division")}
          searchPlaceholder={t("search")}
          emptyText={t("noMatch")}
          value={divisionId}
          onValueChange={(v) => {
            setDivisionId(v);
            setDistrictId("");
            setUpazilaId("");
          }}
          options={(divisions.data ?? []).map((d) => ({ value: d.id, label: label(d.nameEn, d.nameBn) }))}
        />
        <Select
          aria-label={t("district")}
          placeholder={t("district")}
          value={districtId}
          disabled={!divisionId}
          searchable
          searchPlaceholder={t("search")}
          emptyText={t("noMatch")}
          onValueChange={(v) => {
            setDistrictId(v);
            setUpazilaId("");
          }}
          options={(districts.data ?? []).map((d) => ({ value: d.id, label: label(d.nameEn, d.nameBn) }))}
        />
      </div>
      {districtId && (upazilas.data ?? []).length > 0 ? (
        <Select
          aria-label={t("upazila")}
          placeholder={t("upazila")}
          value={upazilaId}
          searchable
          searchPlaceholder={t("search")}
          emptyText={t("noMatch")}
          onValueChange={setUpazilaId}
          options={(upazilas.data ?? []).map((u) => ({ value: u.id, label: label(u.nameEn, u.nameBn) }))}
        />
      ) : null}

      {!districtId ? <p className="text-sm text-muted">{t("choose")}</p> : null}
      {districtId && quote.isPending ? <p className="text-sm text-muted">{t("calculating")}</p> : null}
      {districtId && quote.isError ? <Alert variant="warning">{storeError(quote.error)}</Alert> : null}
      {result ? (
        <div className="space-y-1 text-sm" aria-live="polite" data-testid="delivery-result">
          <p className="text-strong">
            {result.isFree ? <span className="font-semibold text-success-ink">{t("free")}</span> : <>{t("fee")} <span className="font-semibold">{money(result.total)}</span></>}
            <span className="text-muted"> · {label(result.zoneNameEn, result.zoneNameBn)}</span>
          </p>
          <p className="text-muted">
            {t("days", { min: toNumber(result.daysMin), max: toNumber(result.daysMax) })}
            {result.earliestDelivery ? <> · {t("by", { date: format.dateTime(new Date(result.earliestDelivery), { day: "numeric", month: "short" }) })}</> : null}
          </p>
          {!result.isFree && toNumber(result.amountToFreeDelivery) > 0 ? <p className="text-muted">{t("freeAbove", { amount: money(result.amountToFreeDelivery) })}</p> : null}
        </div>
      ) : null}
    </section>
  );
}
