"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Save } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, Switch, Textarea } from "@/components/ui/form";
import { Alert, Badge, Card, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { toNumber } from "@/modules/catalog/format";
import { usePaymentSettings, useSavePaymentSettings, type PaymentSettings } from "./api";
import { usePayMethodLabel, usePaymentErrorText } from "./common";
import { usePaymentPermissions } from "./permissions";

/** The form is mounted once the settings are loaded, so its fields start from the saved values without an effect. */
function SettingsForm({ settings }: { settings: PaymentSettings }) {
  const t = useTranslations("payments.admin.settings");
  const tk = useTranslations("payments.methodKind");
  const methodLabel = usePayMethodLabel();
  const errorText = usePaymentErrorText();
  const perms = usePaymentPermissions();
  const save = useSavePaymentSettings();
  const editable = perms.gatewayEdit;

  const [enabled, setEnabled] = React.useState<Record<string, boolean>>(() => Object.fromEntries(settings.methods.map((m) => [m.code, m.enabled])));
  const [codMax, setCodMax] = React.useState(() => String(toNumber(settings.codMaxAmount)));
  const [instructions, setInstructions] = React.useState(settings.manualInstructions ?? "");
  const [autoConfirm, setAutoConfirm] = React.useState(settings.autoConfirmPaid);
  const [error, setError] = React.useState<string | null>(null);
  const [fieldError, setFieldError] = React.useState<string | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    setFieldError(null);
    const max = Number(codMax || "0");
    if (!Number.isFinite(max) || max < 0) return setFieldError(t("codMaxInvalid"));
    try {
      await save.mutateAsync({
        methods: settings.methods.map((m) => ({ code: m.code, enabled: enabled[m.code] ?? m.enabled })),
        codMaxAmount: max,
        manualInstructions: instructions.trim() || null,
        autoConfirmPaid: autoConfirm,
      });
      toast.success(t("saved"));
    } catch (failure) {
      setError(errorText(failure));
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="space-y-5" aria-label={t("title")}>
      {!editable ? <Alert variant="info">{t("readOnly")}</Alert> : null}

      <Card className="space-y-3 p-4 sm:p-5">
        <h2 className="text-lg font-semibold text-strong">{t("methods")}</h2>
        <p className="text-sm text-muted">{t("methodsHelp")}</p>
        <ul className="divide-y divide-line" data-testid="method-settings">
          {settings.methods.map((m) => (
            <li key={m.code} className="flex items-start justify-between gap-3 py-3" data-method={m.code}>
              <div className="min-w-0 space-y-1">
                <p className="flex flex-wrap items-center gap-2 font-semibold text-strong">
                  <label htmlFor={`method-${m.code}`} className="cursor-pointer">
                    {methodLabel(m.code, m.name)}
                  </label>
                  <Badge variant="outline">{tk(m.kind)}</Badge>
                  {m.enabled && !m.available ? <Badge variant="warning">{t("unavailable")}</Badge> : null}
                </p>
                {m.reason ? <p className="text-sm text-muted [overflow-wrap:anywhere]">{m.reason}</p> : null}
              </div>
              <ChoiceRow htmlFor={`method-${m.code}`} className="shrink-0">
                <Switch id={`method-${m.code}`} checked={enabled[m.code] ?? false} onCheckedChange={(v) => setEnabled((old) => ({ ...old, [m.code]: v }))} disabled={!editable} aria-label={t("enable", { method: methodLabel(m.code, m.name) })} />
              </ChoiceRow>
            </li>
          ))}
        </ul>
      </Card>

      <Card className="space-y-4 p-4 sm:p-5">
        <Field label={t("codMax")} htmlFor="set-cod-max" hint={t("codMaxHint")} error={fieldError ?? undefined}>
          <Input id="set-cod-max" type="number" inputMode="decimal" min="0" step="1" value={codMax} onChange={(e) => setCodMax(e.target.value)} disabled={!editable} aria-invalid={!!fieldError} />
        </Field>
        <Field label={t("instructions")} htmlFor="set-instructions" hint={t("instructionsHint")}>
          <Textarea id="set-instructions" rows={5} maxLength={1500} value={instructions} onChange={(e) => setInstructions(e.target.value)} disabled={!editable} />
        </Field>
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <label htmlFor="set-auto" className="cursor-pointer font-semibold text-strong">
              {t("autoConfirm")}
            </label>
            <p className="text-sm text-muted">{t("autoConfirmHint")}</p>
          </div>
          <ChoiceRow htmlFor="set-auto" className="shrink-0">
            <Switch id="set-auto" checked={autoConfirm} onCheckedChange={setAutoConfirm} disabled={!editable} aria-label={t("autoConfirm")} />
          </ChoiceRow>
        </div>
      </Card>

      {error ? <Alert variant="danger" data-testid="settings-error">{error}</Alert> : null}
      {editable ? (
        <Button type="submit" size="lg" loading={save.isPending} data-testid="settings-save">
          <Save aria-hidden /> {t("save")}
        </Button>
      ) : null}
    </form>
  );
}

export function PaymentSettingsView() {
  const t = useTranslations("payments.admin.common");
  const query = usePaymentSettings();
  if (query.isError) return <Alert variant="danger">{t("loadFailed")}</Alert>;
  if (!query.data) return <Skeleton className="h-64" aria-busy />;
  // A new key remounts the form when the saved values change (after a save), so the fields show what the server holds.
  const key = JSON.stringify([query.data.methods.map((m) => m.enabled), query.data.codMaxAmount, query.data.manualInstructions, query.data.autoConfirmPaid]);
  return <SettingsForm key={key} settings={query.data} />;
}
