"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { KeyRound, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox, ChoiceRow, Field, Input, Label, Switch, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useCourierProviders, useSaveCourier, type Courier } from "./api";
import { MAX_SETTINGS, courierToForm, emptyCourierForm, formToRequest, validateCourierForm, type CourierFormErrors, type CourierFormValues } from "./admin-logic";
import { useDeliveryZones } from "./zones";

type Props = { open: boolean; onOpenChange: (open: boolean) => void; courier: Courier | null };

/** Add or change a courier. Loaded on demand (the list page does not pay for it) and mounted only while open, so it always starts fresh. */
export default function CourierFormDialog({ open, onOpenChange, courier }: Props) {
  const t = useTranslations("couriers.form");
  const tc = useTranslations("couriers.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{courier ? t("editTitle", { name: courier.name }) : t("addTitle")}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{t("description")}</DialogDescription>
        {open ? <Body courier={courier} close={() => onOpenChange(false)} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function Body({ courier, close }: { courier: Courier | null; close: () => void }) {
  const t = useTranslations("couriers.form");
  const te = useTranslations("couriers.form.errors");
  const tc = useTranslations("couriers.common");
  const problemMessage = useProblemMessage();
  const providers = useCourierProviders();
  const zones = useDeliveryZones(true);
  const save = useSaveCourier();
  const isNew = !courier;
  const [values, setValues] = React.useState<CourierFormValues>(() => (courier ? courierToForm(courier) : emptyCourierForm("SANDBOX")));
  const [errors, setErrors] = React.useState<CourierFormErrors>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const [zoneText, setZoneText] = React.useState(() => values.supportedZoneCodes.join(", "));

  const set = <K extends keyof CourierFormValues>(key: K, value: CourierFormValues[K]) => setValues((old) => ({ ...old, [key]: value }));
  const providerOptions = React.useMemo(
    () => (providers.data ?? []).map((p) => ({ value: p.code, label: p.name, hint: p.isConfigured ? undefined : t("notSetUp") })),
    [providers.data, t],
  );
  const provider = providers.data?.find((p) => p.code === values.provider);
  const configured = provider?.isConfigured ?? true;
  const zoneList = zones.data ?? [];
  const zonesFailed = zones.isError;

  const toggleZone = (code: string, on: boolean) => set("supportedZoneCodes", on ? [...new Set([...values.supportedZoneCodes, code])] : values.supportedZoneCodes.filter((c) => c !== code));

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: CourierFormValues = zonesFailed ? { ...values, supportedZoneCodes: zoneText.split(/[\s,;]+/).map((c) => c.trim().toUpperCase()).filter(Boolean) } : values;
    const found = validateCourierForm(next, isNew);
    setErrors(found);
    setFailure(null);
    if (Object.keys(found).length) return;
    try {
      await save.mutateAsync({ id: courier?.id, code: isNew ? next.code.trim().toUpperCase() : undefined, body: formToRequest(next) });
      toast.success(isNew ? t("added") : t("saved"));
      close();
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  const err = (key: keyof CourierFormErrors) => (errors[key] ? te(errors[key]!) : undefined);

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-6">
      <fieldset className="space-y-4">
        <legend className="mb-2 font-semibold text-strong">{t("basics")}</legend>
        <div className="grid gap-4 sm:grid-cols-2">
          {isNew ? (
            <Field label={t("code")} htmlFor="cf-code" required hint={t("codeHint")} error={err("code")}>
              <Input id="cf-code" value={values.code} maxLength={30} onChange={(e) => set("code", e.target.value.toUpperCase())} aria-invalid={!!errors.code} autoComplete="off" />
            </Field>
          ) : null}
          <Field label={t("name")} htmlFor="cf-name" required error={err("name")}>
            <Input id="cf-name" value={values.name} maxLength={100} onChange={(e) => set("name", e.target.value)} aria-invalid={!!errors.name} autoComplete="off" />
          </Field>
          <Field label={t("provider")} htmlFor="cf-provider" required hint={t("providerHint")} error={err("provider")}>
            {providers.isPending ? (
              <Skeleton className="h-12" />
            ) : (
              <Select id="cf-provider" value={values.provider} onValueChange={(v) => set("provider", v)} options={providerOptions} placeholder={t("provider")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.provider} />
            )}
          </Field>
          <Field label={t("priority")} htmlFor="cf-priority" required hint={t("priorityHint")} error={err("priority")}>
            <Input id="cf-priority" inputMode="numeric" value={values.priority} onChange={(e) => set("priority", e.target.value)} aria-invalid={!!errors.priority} autoComplete="off" />
          </Field>
        </div>
        <div>
          <ChoiceRow htmlFor="cf-enabled">
            <Switch id="cf-enabled" checked={values.isEnabled && configured} disabled={!configured} onCheckedChange={(v) => set("isEnabled", v)} />
            {t("enabled")}
          </ChoiceRow>
          {!configured ? <p className="text-sm text-muted">{t("enabledBlocked")}</p> : null}
        </div>
      </fieldset>

      <fieldset className="space-y-4">
        <legend className="mb-2 font-semibold text-strong">{t("rules")}</legend>
        <div className="grid gap-x-6 sm:grid-cols-2">
          <ChoiceRow htmlFor="cf-cod">
            <Checkbox id="cf-cod" checked={values.codSupported} onCheckedChange={(v) => set("codSupported", v === true)} />
            {t("cod")}
          </ChoiceRow>
          <ChoiceRow htmlFor="cf-intl">
            <Checkbox id="cf-intl" checked={values.supportsInternational} onCheckedChange={(v) => set("supportsInternational", v === true)} />
            {t("international")}
          </ChoiceRow>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("minWeight")} htmlFor="cf-min" hint={t("weightHint")} error={err("minWeightGrams")}>
            <Input id="cf-min" inputMode="numeric" value={values.minWeightGrams} onChange={(e) => set("minWeightGrams", e.target.value)} aria-invalid={!!errors.minWeightGrams} autoComplete="off" />
          </Field>
          <Field label={t("maxWeight")} htmlFor="cf-max" hint={t("weightHint")} error={err("maxWeightGrams")}>
            <Input id="cf-max" inputMode="numeric" value={values.maxWeightGrams} onChange={(e) => set("maxWeightGrams", e.target.value)} aria-invalid={!!errors.maxWeightGrams} autoComplete="off" />
          </Field>
        </div>
        <div className="space-y-2">
          <Label id="cf-zones-label">{t("zones")}</Label>
          <p className="text-sm text-muted">{t("zonesHint")}</p>
          {zones.isPending ? (
            <Skeleton className="h-24" />
          ) : zonesFailed ? (
            <Field label={t("zonesTyped")} htmlFor="cf-zone-text" hint={t("zonesTypedHint")}>
              <Input id="cf-zone-text" value={zoneText} onChange={(e) => setZoneText(e.target.value)} autoComplete="off" />
            </Field>
          ) : (
            <div role="group" aria-labelledby="cf-zones-label" className="grid gap-x-6 sm:grid-cols-2">
              {zoneList.map((zone) => (
                <ChoiceRow key={zone.id} htmlFor={`cf-zone-${zone.code}`}>
                  <Checkbox id={`cf-zone-${zone.code}`} checked={values.supportedZoneCodes.includes(zone.code)} onCheckedChange={(v) => toggleZone(zone.code, v === true)} />
                  <span className="min-w-0 [overflow-wrap:anywhere]">
                    {zone.nameEn} <span className="font-mono text-xs text-muted">{zone.code}</span>
                  </span>
                </ChoiceRow>
              ))}
            </div>
          )}
        </div>
      </fieldset>

      <fieldset className="space-y-4">
        <legend className="mb-2 font-semibold text-strong">{t("prices")}</legend>
        <p className="text-sm text-muted">{t("pricesHint")}</p>
        <div className="grid gap-4 sm:grid-cols-3">
          <Field label={t("baseRate")} htmlFor="cf-base" error={err("baseRate")}>
            <Input id="cf-base" inputMode="decimal" value={values.baseRate} onChange={(e) => set("baseRate", e.target.value)} aria-invalid={!!errors.baseRate} autoComplete="off" />
          </Field>
          <Field label={t("perExtraKg")} htmlFor="cf-extra" error={err("perExtraKgRate")}>
            <Input id="cf-extra" inputMode="decimal" value={values.perExtraKgRate} onChange={(e) => set("perExtraKgRate", e.target.value)} aria-invalid={!!errors.perExtraKgRate} autoComplete="off" />
          </Field>
          <Field label={t("baseUpTo")} htmlFor="cf-upto" error={err("baseRateUpToGrams")}>
            <Input id="cf-upto" inputMode="numeric" value={values.baseRateUpToGrams} onChange={(e) => set("baseRateUpToGrams", e.target.value)} aria-invalid={!!errors.baseRateUpToGrams} autoComplete="off" />
          </Field>
        </div>
        <Field label={t("chargeOverride")} htmlFor="cf-override" hint={t("chargeOverrideHint")} error={err("chargeOverride")}>
          <Input id="cf-override" inputMode="decimal" value={values.chargeOverride} onChange={(e) => set("chargeOverride", e.target.value)} aria-invalid={!!errors.chargeOverride} autoComplete="off" />
        </Field>
      </fieldset>

      <fieldset className="space-y-4">
        <legend className="mb-2 font-semibold text-strong">{t("tracking")}</legend>
        <Field label={t("trackingTemplate")} htmlFor="cf-track" hint={t("trackingTemplateHint")} error={err("trackingUrlTemplate")}>
          <Input id="cf-track" type="url" inputMode="url" value={values.trackingUrlTemplate} maxLength={300} onChange={(e) => set("trackingUrlTemplate", e.target.value)} aria-invalid={!!errors.trackingUrlTemplate} autoComplete="off" />
        </Field>
        <Field label={t("notes")} htmlFor="cf-notes">
          <Textarea id="cf-notes" rows={3} maxLength={500} value={values.notes} onChange={(e) => set("notes", e.target.value)} />
        </Field>
      </fieldset>

      <fieldset className="space-y-3">
        <legend className="mb-2 font-semibold text-strong">{t("settings")}</legend>
        <Alert variant="warning" title={t("settingsWarningTitle")}>
          <span className="flex items-start gap-2">
            <KeyRound className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("settingsWarning")}</span>
          </span>
        </Alert>
        {values.settings.map((row, index) => (
          <div key={index} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] items-end gap-2">
            <Field label={t("settingKey")} htmlFor={`cf-sk-${index}`}>
              <Input id={`cf-sk-${index}`} value={row.key} maxLength={60} onChange={(e) => set("settings", values.settings.map((r, i) => (i === index ? { ...r, key: e.target.value } : r)))} autoComplete="off" />
            </Field>
            <Field label={t("settingValue")} htmlFor={`cf-sv-${index}`}>
              <Input id={`cf-sv-${index}`} value={row.value} maxLength={200} onChange={(e) => set("settings", values.settings.map((r, i) => (i === index ? { ...r, value: e.target.value } : r)))} autoComplete="off" />
            </Field>
            <Button type="button" variant="ghost" size="icon" aria-label={t("removeSetting", { key: row.key || String(index + 1) })} onClick={() => set("settings", values.settings.filter((_, i) => i !== index))}>
              <Trash2 aria-hidden />
            </Button>
          </div>
        ))}
        {errors.settings ? (
          <p role="alert" className="text-sm font-medium text-danger">
            {te(errors.settings)}
          </p>
        ) : null}
        <Button type="button" variant="secondary" disabled={values.settings.length >= MAX_SETTINGS} onClick={() => set("settings", [...values.settings, { key: "", value: "" }])}>
          <Plus aria-hidden /> {t("addSetting")}
        </Button>
      </fieldset>

      {failure ? (
        <Alert variant="danger" data-testid="courier-form-error">
          {failure}
        </Alert>
      ) : null}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="courier-save">
          {isNew ? t("add") : t("save")}
        </Button>
      </div>
    </form>
  );
}
