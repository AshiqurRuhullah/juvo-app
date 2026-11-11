"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useLocale, useTranslations } from "next-intl";
import { LocateFixed } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, Label, RadioGroup, RadioItem } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { toNumber } from "@/modules/catalog/format";
import { useCountries, useDistricts, useDivisions, useUpazilas } from "@/modules/store/api";
import type { AddressDraft } from "./address";

const PinMap = dynamic(() => import("@/modules/myaccount/pin-map"), { ssr: false, loading: () => <Skeleton className="h-64 w-full sm:h-72" /> });

/**
 * The address fields of the checkout and of the social order desk: recipient, phone, place (Bangladesh by division, district and
 * upazila, or another enabled country), street, landmark and an optional map pin. The parent owns the values and the error texts.
 */
export function AddressFields({
  value,
  onChange,
  errors,
  idPrefix,
  showPin = true,
  phoneRequired = true,
}: {
  value: AddressDraft;
  onChange: (next: AddressDraft) => void;
  errors: Record<string, string | undefined>;
  idPrefix: string;
  showPin?: boolean;
  phoneRequired?: boolean;
}) {
  const t = useTranslations("orders.address");
  const locale = useLocale();
  const name = (en: string, bn: string | null | undefined) => (locale === "bn" && bn ? bn : en);
  const set = (patch: Partial<AddressDraft>) => onChange({ ...value, ...patch });
  const divisions = useDivisions();
  const districts = useDistricts(value.divisionId || undefined);
  const upazilas = useUpazilas(value.districtId || undefined);
  const countries = useCountries();
  const [locating, setLocating] = React.useState(false);
  const foreign = (countries.data ?? []).filter((c) => c.isEnabled && c.code !== "BD");
  const district = districts.data?.find((d) => d.id === value.districtId);
  const center: [number, number] | null = district && district.latitude != null && district.longitude != null ? [toNumber(district.latitude), toNumber(district.longitude)] : null;
  const id = (key: string) => `${idPrefix}-${key}`;

  const locate = () => {
    if (!("geolocation" in navigator)) return toast.error(t("locationUnavailable"));
    setLocating(true);
    navigator.geolocation.getCurrentPosition(
      (pos) => {
        setLocating(false);
        set({ latitude: Math.round(pos.coords.latitude * 1e6) / 1e6, longitude: Math.round(pos.coords.longitude * 1e6) / 1e6 });
      },
      () => {
        setLocating(false);
        toast.error(t("locationDenied"));
      },
      { enableHighAccuracy: true, timeout: 10_000 },
    );
  };

  return (
    <div className="space-y-4">
      {foreign.length > 0 ? (
        <RadioGroup value={value.mode} onValueChange={(v) => set({ mode: v === "abroad" ? "abroad" : "bd" })} className="grid grid-cols-2 gap-2" aria-label={t("where")}>
          {(["bd", "abroad"] as const).map((mode) => (
            <div key={mode} className="flex min-h-12 items-center gap-3 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
              <RadioItem id={id(`mode-${mode}`)} value={mode} />
              <Label htmlFor={id(`mode-${mode}`)} className="flex min-h-11 flex-1 cursor-pointer items-center">
                {mode === "bd" ? t("bangladesh") : t("abroad")}
              </Label>
            </div>
          ))}
        </RadioGroup>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("recipient")} htmlFor={id("recipient")} required error={errors.recipient}>
          <Input id={id("recipient")} value={value.recipientName} onChange={(e) => set({ recipientName: e.target.value })} autoComplete="name" aria-invalid={!!errors.recipient} />
        </Field>
        <Field label={t("phone")} htmlFor={id("phone")} required={phoneRequired} hint={value.mode === "bd" ? t("phoneHintBd") : t("phoneHintAbroad")} error={errors.phone}>
          <Input id={id("phone")} type="tel" inputMode="tel" value={value.phone} onChange={(e) => set({ phone: e.target.value })} autoComplete="tel" aria-invalid={!!errors.phone} />
        </Field>
      </div>

      {value.mode === "bd" ? (
        <>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t("division")} htmlFor={id("division")}>
              <Select
                id={id("division")}
                value={value.divisionId}
                onValueChange={(v) => set({ divisionId: v, districtId: "", upazilaId: "" })}
                options={(divisions.data ?? []).map((d) => ({ value: d.id, label: name(d.nameEn, d.nameBn) }))}
                placeholder={t("choose")}
                searchPlaceholder={t("search")}
                emptyText={t("noMatch")}
              />
            </Field>
            <Field label={t("district")} htmlFor={id("district")} required error={errors.district}>
              <Select
                id={id("district")}
                value={value.districtId}
                onValueChange={(v) => set({ districtId: v, upazilaId: "" })}
                options={(districts.data ?? []).map((d) => ({ value: d.id, label: name(d.nameEn, d.nameBn) }))}
                placeholder={t("choose")}
                searchPlaceholder={t("search")}
                emptyText={t("noMatch")}
                disabled={!value.divisionId}
                invalid={!!errors.district}
              />
            </Field>
            <Field label={t("upazila")} htmlFor={id("upazila")}>
              <Select
                id={id("upazila")}
                value={value.upazilaId}
                onValueChange={(v) => set({ upazilaId: v })}
                options={(upazilas.data ?? []).map((u) => ({ value: u.id, label: name(u.nameEn, u.nameBn) }))}
                placeholder={t("choose")}
                searchPlaceholder={t("search")}
                emptyText={t("noMatch")}
                disabled={!value.districtId}
              />
            </Field>
          </div>
          <Field label={t("area")} htmlFor={id("area")} hint={t("areaHint")}>
            <Input id={id("area")} value={value.area} onChange={(e) => set({ area: e.target.value })} />
          </Field>
        </>
      ) : (
        <>
          <Field label={t("country")} htmlFor={id("country")} required error={errors.country}>
            <Select
              id={id("country")}
              value={value.countryCode}
              onValueChange={(v) => set({ countryCode: v })}
              options={foreign.map((c) => ({ value: c.code, label: name(c.nameEn, c.nameBn) }))}
              placeholder={t("choose")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              invalid={!!errors.country}
            />
          </Field>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t("city")} htmlFor={id("city")} required error={errors.city}>
              <Input id={id("city")} value={value.city} onChange={(e) => set({ city: e.target.value })} autoComplete="address-level2" aria-invalid={!!errors.city} />
            </Field>
            <Field label={t("state")} htmlFor={id("state")}>
              <Input id={id("state")} value={value.stateProvince} onChange={(e) => set({ stateProvince: e.target.value })} autoComplete="address-level1" />
            </Field>
            <Field label={t("postal")} htmlFor={id("postal")}>
              <Input id={id("postal")} value={value.postalCode} onChange={(e) => set({ postalCode: e.target.value })} autoComplete="postal-code" />
            </Field>
          </div>
        </>
      )}

      <Field label={t("line")} htmlFor={id("line")} required hint={t("lineHint")} error={errors.line}>
        <Input id={id("line")} value={value.addressLine} onChange={(e) => set({ addressLine: e.target.value })} autoComplete="street-address" aria-invalid={!!errors.line} />
      </Field>
      <Field label={t("landmark")} htmlFor={id("landmark")} hint={t("landmarkHint")}>
        <Input id={id("landmark")} value={value.landmark} onChange={(e) => set({ landmark: e.target.value })} />
      </Field>

      {showPin && value.mode === "bd" ? (
        <div className="space-y-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <p className="font-medium text-strong">{t("pin")}</p>
              <p className="text-sm text-muted">{t("pinHint")}</p>
            </div>
            <Button type="button" variant="secondary" onClick={locate} loading={locating}>
              <LocateFixed aria-hidden /> {t("useMyLocation")}
            </Button>
          </div>
          <PinMap latitude={value.latitude} longitude={value.longitude} center={center} label={t("mapLabel")} onChange={(lat, lng) => set({ latitude: lat, longitude: lng })} />
          {value.latitude !== null && value.longitude !== null ? (
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted">
              <span>
                {value.latitude.toFixed(5)}, {value.longitude.toFixed(5)}
              </span>
              <Button type="button" variant="ghost" onClick={() => set({ latitude: null, longitude: null })}>
                {t("removePin")}
              </Button>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
