"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useLocale, useTranslations } from "next-intl";
import { LocateFixed } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, Label, RadioGroup, RadioItem, Switch } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useReportCartError } from "@/modules/cart/use-cart-error";
import { toNumber } from "@/modules/catalog/format";
import { useCountries, useCreateAddress, useDistricts, useDivisions, useUpazilas, useUpdateAddress, type Address, type SaveAddress } from "@/modules/store/api";

const PinMap = dynamic(() => import("./pin-map"), { ssr: false, loading: () => <Skeleton className="h-64 w-full sm:h-72" /> });

const LABELS = ["Home", "Office", "Other"] as const;
const BD_PHONE = /^(?:\+?88)?01[3-9]\d{8}$/;

type Mode = "bd" | "abroad";

/** The body is mounted only while the dialog is open, so every opening starts from the address being edited (or a blank form). */
function AddressForm({ address, first, onDone }: { address: Address | null; first: boolean; onDone: () => void }) {
  const t = useTranslations("myaccount.addresses");
  const locale = useLocale();
  const name = (en: string, bn: string | null | undefined) => (locale === "bn" && bn ? bn : en);
  const report = useReportCartError();
  const create = useCreateAddress();
  const update = useUpdateAddress();

  const [mode, setMode] = React.useState<Mode>(address && address.countryCode !== "BD" ? "abroad" : "bd");
  const [label, setLabel] = React.useState<string>(address?.label ?? "Home");
  const [recipientName, setRecipientName] = React.useState(address?.recipientName ?? "");
  const [phone, setPhone] = React.useState(address?.phone ?? "");
  const [divisionId, setDivisionId] = React.useState(address?.divisionId ?? "");
  const [districtId, setDistrictId] = React.useState(address?.districtId ?? "");
  const [upazilaId, setUpazilaId] = React.useState(address?.upazilaId ?? "");
  const [area, setArea] = React.useState(address?.area ?? "");
  const [addressLine, setAddressLine] = React.useState(address?.addressLine ?? "");
  const [landmark, setLandmark] = React.useState(address?.landmark ?? "");
  const [countryCode, setCountryCode] = React.useState(address && address.countryCode !== "BD" ? address.countryCode : "");
  const [city, setCity] = React.useState(address?.city ?? "");
  const [stateProvince, setStateProvince] = React.useState(address?.stateProvince ?? "");
  const [postalCode, setPostalCode] = React.useState(address?.postalCode ?? "");
  const [latitude, setLatitude] = React.useState<number | null>(address?.latitude != null ? toNumber(address.latitude) : null);
  const [longitude, setLongitude] = React.useState<number | null>(address?.longitude != null ? toNumber(address.longitude) : null);
  const [isDefault, setIsDefault] = React.useState(address?.isDefault ?? first);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [locating, setLocating] = React.useState(false);

  const divisions = useDivisions();
  const districts = useDistricts(divisionId || undefined);
  const upazilas = useUpazilas(districtId || undefined);
  const countries = useCountries();
  const district = districts.data?.find((d) => d.id === districtId);
  const center: [number, number] | null = district && district.latitude != null && district.longitude != null ? [toNumber(district.latitude), toNumber(district.longitude)] : null;
  const pending = create.isPending || update.isPending;

  const locate = () => {
    if (!("geolocation" in navigator)) {
      toast.error(t("locationUnavailable"));
      return;
    }
    setLocating(true);
    navigator.geolocation.getCurrentPosition(
      (pos) => {
        setLocating(false);
        setLatitude(Math.round(pos.coords.latitude * 1e6) / 1e6);
        setLongitude(Math.round(pos.coords.longitude * 1e6) / 1e6);
      },
      () => {
        setLocating(false);
        toast.error(t("locationDenied"));
      },
      { enableHighAccuracy: true, timeout: 10_000 },
    );
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    if (!recipientName.trim()) next.recipientName = t("errors.recipient");
    if (mode === "bd") {
      if (!BD_PHONE.test(phone.replace(/[\s-]/g, ""))) next.phone = t("errors.phoneBd");
      if (!districtId) next.districtId = t("errors.district");
      if (!landmark.trim()) next.landmark = t("errors.landmark");
    } else {
      if (phone.replace(/\D/g, "").length < 6) next.phone = t("errors.phoneAbroad");
      if (!countryCode) next.countryCode = t("errors.country");
      if (!city.trim()) next.city = t("errors.city");
    }
    if (!addressLine.trim()) next.addressLine = t("errors.line");
    setErrors(next);
    if (Object.keys(next).length) return;

    const body: SaveAddress =
      mode === "bd"
        ? { label, recipientName: recipientName.trim(), phone: phone.replace(/[\s-]/g, ""), countryCode: "BD", districtId, upazilaId: upazilaId || null, area: area.trim() || null, addressLine: addressLine.trim(), landmark: landmark.trim(), stateProvince: null, city: null, postalCode: null, latitude, longitude, isDefault }
        : { label, recipientName: recipientName.trim(), phone: phone.trim(), countryCode, districtId: null, upazilaId: null, area: null, addressLine: addressLine.trim(), landmark: landmark.trim() || null, stateProvince: stateProvince.trim() || null, city: city.trim(), postalCode: postalCode.trim() || null, latitude: null, longitude: null, isDefault };
    try {
      if (address) await update.mutateAsync({ id: address.id, body });
      else await create.mutateAsync(body);
      toast.success(t("saved"));
      onDone();
    } catch (failure) {
      setErrors(report(failure));
    }
  };

  const foreign = (countries.data ?? []).filter((c) => c.isEnabled && c.code !== "BD");
  const err = (key: string) => errors[key];

  return (
    <form onSubmit={submit} noValidate className="mt-4 space-y-4">
      <Field label={t("label")} htmlFor="addr-label">
        <RadioGroup value={label} onValueChange={setLabel} className="grid grid-cols-3 gap-2" aria-label={t("label")}>
          {LABELS.map((value) => (
            <div key={value} className="flex min-h-12 items-center gap-2 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
              <RadioItem id={`addr-label-${value}`} value={value} />
              <Label htmlFor={`addr-label-${value}`} className="flex min-h-11 flex-1 cursor-pointer items-center">
                {t(`labels.${value}`)}
              </Label>
            </div>
          ))}
        </RadioGroup>
      </Field>

      {foreign.length > 0 ? (
        <RadioGroup value={mode} onValueChange={(v) => setMode(v as Mode)} className="grid grid-cols-2 gap-2" aria-label={t("where")}>
          {(["bd", "abroad"] as const).map((value) => (
            <div key={value} className="flex min-h-12 items-center gap-2 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
              <RadioItem id={`addr-mode-${value}`} value={value} />
              <Label htmlFor={`addr-mode-${value}`} className="flex min-h-11 flex-1 cursor-pointer items-center">
                {value === "bd" ? t("bangladesh") : t("abroad")}
              </Label>
            </div>
          ))}
        </RadioGroup>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("recipient")} htmlFor="addr-recipient" required error={err("recipientName")}>
          <Input id="addr-recipient" value={recipientName} onChange={(e) => setRecipientName(e.target.value)} autoComplete="name" aria-invalid={!!err("recipientName")} />
        </Field>
        <Field label={t("phone")} htmlFor="addr-phone" required hint={mode === "bd" ? t("phoneHintBd") : t("phoneHintAbroad")} error={err("phone")}>
          <Input id="addr-phone" type="tel" inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} autoComplete="tel" aria-invalid={!!err("phone")} />
        </Field>
      </div>

      {mode === "bd" ? (
        <>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t("division")} htmlFor="addr-division">
              <Select
                id="addr-division"
                value={divisionId}
                onValueChange={(v) => {
                  setDivisionId(v);
                  setDistrictId("");
                  setUpazilaId("");
                }}
                options={(divisions.data ?? []).map((d) => ({ value: d.id, label: name(d.nameEn, d.nameBn) }))}
                placeholder={t("choose")}
                searchPlaceholder={t("search")}
                emptyText={t("noMatch")}
              />
            </Field>
            <Field label={t("district")} htmlFor="addr-district" required error={err("districtId")}>
              <Select
                id="addr-district"
                value={districtId}
                onValueChange={(v) => {
                  setDistrictId(v);
                  setUpazilaId("");
                }}
                options={(districts.data ?? []).map((d) => ({ value: d.id, label: name(d.nameEn, d.nameBn) }))}
                placeholder={t("choose")}
                searchPlaceholder={t("search")}
                emptyText={t("noMatch")}
                disabled={!divisionId}
                invalid={!!err("districtId")}
              />
            </Field>
            <Field label={t("upazila")} htmlFor="addr-upazila">
              <Select
                id="addr-upazila"
                value={upazilaId}
                onValueChange={setUpazilaId}
                options={(upazilas.data ?? []).map((u) => ({ value: u.id, label: name(u.nameEn, u.nameBn) }))}
                placeholder={t("choose")}
                searchPlaceholder={t("search")}
                emptyText={t("noMatch")}
                disabled={!districtId}
              />
            </Field>
          </div>
          <Field label={t("area")} htmlFor="addr-area" hint={t("areaHint")}>
            <Input id="addr-area" value={area} onChange={(e) => setArea(e.target.value)} />
          </Field>
        </>
      ) : (
        <>
          <Field label={t("country")} htmlFor="addr-country" required error={err("countryCode")}>
            <Select
              id="addr-country"
              value={countryCode}
              onValueChange={setCountryCode}
              options={foreign.map((c) => ({ value: c.code, label: name(c.nameEn, c.nameBn) }))}
              placeholder={t("choose")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              invalid={!!err("countryCode")}
            />
          </Field>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t("city")} htmlFor="addr-city" required error={err("city")}>
              <Input id="addr-city" value={city} onChange={(e) => setCity(e.target.value)} autoComplete="address-level2" aria-invalid={!!err("city")} />
            </Field>
            <Field label={t("state")} htmlFor="addr-state">
              <Input id="addr-state" value={stateProvince} onChange={(e) => setStateProvince(e.target.value)} autoComplete="address-level1" />
            </Field>
            <Field label={t("postal")} htmlFor="addr-postal">
              <Input id="addr-postal" value={postalCode} onChange={(e) => setPostalCode(e.target.value)} autoComplete="postal-code" />
            </Field>
          </div>
        </>
      )}

      <Field label={t("line")} htmlFor="addr-line" required hint={t("lineHint")} error={err("addressLine")}>
        <Input id="addr-line" value={addressLine} onChange={(e) => setAddressLine(e.target.value)} autoComplete="street-address" aria-invalid={!!err("addressLine")} />
      </Field>
      <Field label={t("landmark")} htmlFor="addr-landmark" required={mode === "bd"} hint={t("landmarkHint")} error={err("landmark")}>
        <Input id="addr-landmark" value={landmark} onChange={(e) => setLandmark(e.target.value)} aria-invalid={!!err("landmark")} />
      </Field>

      {mode === "bd" ? (
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
          <PinMap latitude={latitude} longitude={longitude} center={center} label={t("mapLabel")} onChange={(lat, lng) => (setLatitude(lat), setLongitude(lng))} />
          {latitude !== null && longitude !== null ? (
            <div className="flex flex-wrap items-center justify-between gap-2 text-sm text-muted">
              <span data-testid="pin-coordinates">
                {latitude.toFixed(5)}, {longitude.toFixed(5)}
              </span>
              <Button type="button" variant="ghost" onClick={() => (setLatitude(null), setLongitude(null))}>
                {t("removePin")}
              </Button>
            </div>
          ) : null}
        </div>
      ) : null}

      <div className="flex min-h-11 items-center justify-between gap-3">
        <Label htmlFor="addr-default" className="flex min-h-11 flex-1 cursor-pointer items-center">
          {t("makeDefault")}
        </Label>
        <Switch id="addr-default" checked={isDefault} onCheckedChange={setIsDefault} disabled={address?.isDefault} />
      </div>

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="secondary" onClick={onDone} disabled={pending}>
          {t("cancel")}
        </Button>
        <Button type="submit" loading={pending}>
          {t("save")}
        </Button>
      </div>
    </form>
  );
}

export function AddressDialog({ open, onOpenChange, address, first }: { open: boolean; onOpenChange: (open: boolean) => void; address: Address | null; first: boolean }) {
  const t = useTranslations("myaccount.addresses");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="lg" closeLabel={t("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{address ? t("editTitle") : t("addTitle")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("dialogHelp")}</DialogDescription>
        {open ? <AddressForm address={address} first={first} onDone={() => onOpenChange(false)} /> : null}
      </ModalContent>
    </Dialog>
  );
}
