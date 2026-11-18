"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Save } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, Label, RadioGroup, RadioItem, Switch } from "@/components/ui/form";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useMoney } from "@/modules/catalog/format";
import { useLoyaltySettings, useSaveSettings } from "./api";
import { useLoyaltyErrorText, useLoyaltyPermissions } from "./common";
import { earnExample, isSettingsChanged, ROUNDINGS, SETTING_FIELDS, toSettingsForm, validateSettingsForm, type LoyaltySettings, type SettingField, type SettingsForm } from "./logic";

/** Staff `Settings` tab: the feature switch and every rule the API has (FR-AB01..AB03). Changing anything needs `loyalty.points.adjust`. */
export default function SettingsView() {
  const t = useTranslations("loyalty.settings");
  const query = useLoyaltySettings();
  if (query.isPending) return <Skeleton className="h-96" aria-busy />;
  if (query.isError || !query.data) return <Alert variant="danger">{t("loadFailed")}</Alert>;
  // Mounted again after each save (new data object), so the form always starts from what is stored.
  return <SettingsForm key={JSON.stringify(query.data)} settings={query.data} />;
}

const UNITS: Partial<Record<SettingField["key"], "money" | "percent" | "months" | "days" | "points">> = {
  maxTenderPercent: "percent",
  pointsExpiryMonths: "months",
  expiryWarningDays: "days",
  minPointsToRedeem: "points",
  pointValue: "money",
  referralRewardReferrer: "money",
  referralRewardReferee: "money",
  giftCardMinAmount: "money",
  giftCardMaxAmount: "money",
  giftCardDefaultValidityMonths: "months",
};

function SettingsForm({ settings }: { settings: LoyaltySettings }) {
  const t = useTranslations("loyalty.settings");
  const money = useMoney();
  const perms = useLoyaltyPermissions();
  const save = useSaveSettings();
  const errorText = useLoyaltyErrorText();
  const [form, setForm] = React.useState<SettingsForm>(() => toSettingsForm(settings));
  const [shown, setShown] = React.useState(false);
  const [failure, setFailure] = React.useState<string | null>(null);
  const errors = validateSettingsForm(form);
  const changed = isSettingsChanged(settings, form);
  const readOnly = !perms.pointsAdjust;
  const rate = Number(form.earnRatePerBdt.replace(",", "."));
  const example = Number.isFinite(rate) ? earnExample(rate) : null;

  const set = <K extends keyof SettingsForm>(key: K, value: SettingsForm[K]) => setForm((old) => ({ ...old, [key]: value }));

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setShown(true);
    setFailure(null);
    if (Object.keys(errors).length > 0) return;
    try {
      await save.mutateAsync({ original: settings, form });
      toast.success(t("saved"));
    } catch (error) {
      setFailure(errorText(error));
    }
  };

  const field = (key: SettingField["key"], extra?: { hint?: string }) => {
    const unit = UNITS[key];
    const error = shown && errors[key] ? t(`errors.${errors[key]}`) : undefined;
    const def = SETTING_FIELDS.find((f) => f.key === key)!;
    return (
      <Field key={key} label={t(`fields.${key}`)} htmlFor={`ls-${key}`} hint={extra?.hint ?? t(`hints.${key}`)} error={error}>
        <div className="flex items-center gap-2">
          <Input
            id={`ls-${key}`}
            inputMode={def.whole ? "numeric" : "decimal"}
            value={form[key]}
            onChange={(e) => set(key, e.target.value)}
            disabled={readOnly}
            aria-invalid={!!error}
            autoComplete="off"
            data-testid={`setting-${key}`}
          />
          {unit ? <span className="shrink-0 text-sm text-muted">{t(`units.${unit}`)}</span> : null}
        </div>
      </Field>
    );
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="space-y-6" aria-label={t("title")} data-testid="loyalty-settings">
      {readOnly ? <Alert variant="info">{t("readOnly")}</Alert> : null}

      <section className="rounded-card border border-line bg-card p-4 sm:p-5" aria-labelledby="ls-switch">
        <h2 id="ls-switch" className="text-lg font-semibold text-strong">
          <ChoiceRow htmlFor="ls-enabled" className="justify-between gap-4 font-semibold">
            <span>{t("enabled.title")}</span>
            <Switch id="ls-enabled" checked={form.enabled} onCheckedChange={(v) => set("enabled", v)} disabled={readOnly} data-testid="setting-enabled" />
          </ChoiceRow>
        </h2>
        <p className="mt-1 text-sm text-muted" data-testid="enabled-consequence">
          {form.enabled ? t("enabled.on") : t("enabled.off")}
        </p>
        <p className="mt-3 text-sm font-medium text-strong" data-testid="enabled-state">
          {form.enabled ? t("enabled.stateOn") : t("enabled.stateOff")}
        </p>
      </section>

      <fieldset className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5">
        <legend className="px-1 text-lg font-semibold text-strong">{t("groups.points")}</legend>
        <div className="grid gap-4 sm:grid-cols-2">
          {field("earnRatePerBdt", { hint: example ? t("hints.earnRatePerBdtExample", { points: example.points, amount: money(example.perBdt) }) : t("hints.earnRatePerBdt") })}
          <div className="space-y-1.5">
            <Label id="ls-rounding-label">{t("fields.earnRounding")}</Label>
            <RadioGroup value={form.earnRounding} onValueChange={(v) => set("earnRounding", v as SettingsForm["earnRounding"])} aria-labelledby="ls-rounding-label" disabled={readOnly} className="grid gap-2">
              {ROUNDINGS.map((r) => (
                <div key={r} className="flex min-h-11 items-center gap-3 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                  <RadioItem id={`ls-round-${r}`} value={r} />
                  <Label htmlFor={`ls-round-${r}`} className="flex min-h-11 flex-1 cursor-pointer items-center font-normal">
                    {t(`rounding.${r}`)}
                  </Label>
                </div>
              ))}
            </RadioGroup>
          </div>
          {field("pointValue")}
          {field("minPointsToRedeem")}
          {field("maxTenderPercent")}
          {field("pointsExpiryMonths")}
          {field("expiryWarningDays")}
        </div>
      </fieldset>

      <fieldset className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5">
        <legend className="px-1 text-lg font-semibold text-strong">{t("groups.referrals")}</legend>
        <div className="grid gap-4 sm:grid-cols-2">
          {field("referralRewardReferrer")}
          {field("referralRewardReferee")}
          {field("referralMaxRewardsPerReferrer")}
        </div>
      </fieldset>

      <fieldset className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5">
        <legend className="px-1 text-lg font-semibold text-strong">{t("groups.giftCards")}</legend>
        <div className="grid gap-4 sm:grid-cols-2">
          {field("giftCardMinAmount")}
          {field("giftCardMaxAmount")}
          {field("giftCardDefaultValidityMonths")}
        </div>
      </fieldset>

      {shown && Object.keys(errors).length > 0 ? <Alert variant="danger">{t("fix")}</Alert> : null}
      {failure ? (
        <Alert variant="danger" data-testid="settings-error">
          {failure}
        </Alert>
      ) : null}

      {!readOnly ? (
        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" loading={save.isPending} disabled={!changed} data-testid="settings-save">
            <Save aria-hidden /> {t("save")}
          </Button>
          {!changed ? <span className="text-sm text-muted">{t("noChange")}</span> : null}
        </div>
      ) : null}
    </form>
  );
}
