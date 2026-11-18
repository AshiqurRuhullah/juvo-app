"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useMarketingSettings, useUpdateMarketingSettings } from "./api";
import { useMarketingPermissions } from "./common";
import { SETTINGS_RANGES, settingsFormFrom, settingsToRequest, validateSettingsForm, type SettingsErrors, type SettingsForm } from "./logic";

/** Sending rules for all campaigns and automations: throttle, quiet hours (Dhaka time, SMS only), frequency cap and attribution window. */
export function MarketingSettingsView() {
  const t = useTranslations("marketing.settings");
  const query = useMarketingSettings();
  if (query.isError) return <Alert variant="danger">{t("loadFailed")}</Alert>;
  if (!query.data) return <Skeleton className="h-64" aria-busy="true" />;
  return <SettingsEditor initial={settingsFormFrom(query.data)} />;
}

function SettingsEditor({ initial }: { initial: SettingsForm }) {
  const t = useTranslations("marketing.settings");
  const problemMessage = useProblemMessage();
  const perms = useMarketingPermissions();
  const save = useUpdateMarketingSettings();
  const [form, setForm] = React.useState<SettingsForm>(initial);
  const [baseline, setBaseline] = React.useState(() => JSON.stringify(initial));
  const [errors, setErrors] = React.useState<SettingsErrors>({});
  const [serverErrors, setServerErrors] = React.useState<Record<string, string>>({});
  const [problem, setProblem] = React.useState<string | null>(null);
  const editable = perms.campaignSend;
  const dirty = JSON.stringify(form) !== baseline;
  const set = <K extends keyof SettingsForm>(key: K, value: SettingsForm[K]) => setForm((f) => ({ ...f, [key]: value }));
  const error = (field: keyof SettingsForm) => {
    const e = errors[field];
    if (!e) return serverErrors[field];
    const range = (SETTINGS_RANGES as Record<string, readonly [number, number]>)[field];
    return t(`errors.${e}`, { min: range?.[0] ?? 0, max: range?.[1] ?? 0 });
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateSettingsForm(form);
    setErrors(found);
    setServerErrors({});
    setProblem(null);
    if (Object.keys(found).length > 0) return;
    try {
      const saved = await save.mutateAsync(settingsToRequest(form));
      const next = settingsFormFrom(saved);
      setForm(next);
      setBaseline(JSON.stringify(next));
      toast.success(t("saved"));
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.errors) setServerErrors(fieldErrors(p));
      setProblem(problemMessage(p));
    }
  };

  const num = (field: "frequencyCapMessages" | "frequencyCapDays" | "throttlePerMinute" | "attributionWindowDays", hint: string) => (
    <Field label={t(`fields.${field}`)} htmlFor={`set-${field}`} required hint={hint} error={error(field)}>
      <Input id={`set-${field}`} inputMode="numeric" value={form[field]} onChange={(e) => set(field, e.target.value)} aria-invalid={!!error(field)} />
    </Field>
  );

  return (
    <form onSubmit={submit} noValidate className="max-w-3xl space-y-6" data-testid="marketing-settings-form">
      <p className="text-muted">{t("subtitle")}</p>
      {problem ? <Alert variant="danger">{problem}</Alert> : null}
      {!editable ? <Alert variant="info">{t("readOnly")}</Alert> : null}
      <fieldset disabled={!editable} className="min-w-0 space-y-6 border-0 p-0">
        <section aria-labelledby="set-quiet" className="space-y-3 rounded-card border border-line bg-card p-4 sm:p-5">
          <h2 id="set-quiet" className="font-display text-lg font-semibold text-strong">
            {t("quietTitle")}
          </h2>
          <Alert variant="info" data-testid="quiet-scope">{t("quietScope")}</Alert>
          <ChoiceRow htmlFor="set-quiet-on">
            <Switch id="set-quiet-on" checked={form.quietHoursEnabled} onCheckedChange={(v) => set("quietHoursEnabled", v)} />
            <span>{t("quietEnabled")}</span>
          </ChoiceRow>
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("fields.quietHoursStart")} htmlFor="set-quiet-start" required hint={t("timeHint")} error={error("quietHoursStart")}>
              <Input id="set-quiet-start" type="time" value={form.quietHoursStart} disabled={!form.quietHoursEnabled} onChange={(e) => set("quietHoursStart", e.target.value)} aria-invalid={!!error("quietHoursStart")} />
            </Field>
            <Field label={t("fields.quietHoursEnd")} htmlFor="set-quiet-end" required hint={t("timeHint")} error={error("quietHoursEnd")}>
              <Input id="set-quiet-end" type="time" value={form.quietHoursEnd} disabled={!form.quietHoursEnabled} onChange={(e) => set("quietHoursEnd", e.target.value)} aria-invalid={!!error("quietHoursEnd")} />
            </Field>
          </div>
        </section>

        <section aria-labelledby="set-cap" className="space-y-3 rounded-card border border-line bg-card p-4 sm:p-5">
          <h2 id="set-cap" className="font-display text-lg font-semibold text-strong">
            {t("capTitle")}
          </h2>
          <p className="text-sm text-muted">{t("capIntro")}</p>
          <div className="grid gap-4 sm:grid-cols-2">
            {num("frequencyCapMessages", t("hints.frequencyCapMessages"))}
            {num("frequencyCapDays", t("hints.frequencyCapDays"))}
          </div>
        </section>

        <section aria-labelledby="set-send" className="space-y-3 rounded-card border border-line bg-card p-4 sm:p-5">
          <h2 id="set-send" className="font-display text-lg font-semibold text-strong">
            {t("sendTitle")}
          </h2>
          <div className="grid gap-4 sm:grid-cols-2">
            {num("throttlePerMinute", t("hints.throttlePerMinute"))}
            {num("attributionWindowDays", t("hints.attributionWindowDays"))}
          </div>
        </section>
      </fieldset>
      {editable ? (
        <Button type="submit" loading={save.isPending} disabled={!dirty} data-testid="settings-save">
          {t("save")}
        </Button>
      ) : null}
    </form>
  );
}
