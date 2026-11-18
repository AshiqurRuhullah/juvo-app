"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { FlaskConical, Settings } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ChoiceRow, Field, Input, Switch, RadioGroup, RadioItem } from "@/components/ui/form";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useAutomationResults, useAutomations, useDryRun, useMarketingSettings, useUpdateAutomation, type Automation, type AutomationDryRun } from "./api";
import { MessageBox } from "./campaign-form";
import { Stat, useMarketingPermissions, useReasonLabel } from "./common";
import {
  DELAY_MEANING,
  automationFormFrom,
  automationPatch,
  delayParts,
  needsConsent,
  usesWindow,
  validateAutomationForm,
  type AutomationErrors,
  type AutomationForm,
  type MarketingChannel,
} from "./logic";

/** The four automations (FR-AC03): switch, delay, window, cap and texts for each, a dry run and the results. All are off until someone switches them on. */
export function AutomationsView() {
  const t = useTranslations("marketing.automations");
  const perms = useMarketingPermissions();
  const query = useAutomations();
  const settings = useMarketingSettings();
  const dryRun = useDryRun();
  const problemMessage = useProblemMessage();
  const dateTime = useDateTime();
  const [dry, setDry] = React.useState<AutomationDryRun | null>(null);

  const run = async () => {
    try {
      setDry(await dryRun.mutateAsync());
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  const s = settings.data;
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-muted">{t("subtitle")}</p>
        <div className="flex flex-wrap gap-2">
          {perms.campaignView ? (
            <Button asChild variant="secondary">
              <Link href="/admin/marketing/settings">
                <Settings aria-hidden /> {t("settings")}
              </Link>
            </Button>
          ) : null}
          <Button variant="secondary" onClick={() => void run()} loading={dryRun.isPending} data-testid="dry-run">
            <FlaskConical aria-hidden /> {t("dryRun")}
          </Button>
        </div>
      </div>

      <Alert variant="info" data-testid="quiet-hours-note">
        {s?.quietHoursEnabled ? t("quietHoursOn", { start: s.quietHoursStart, end: s.quietHoursEnd }) : t("quietHoursOff")} {t("quietHoursScope")}
      </Alert>

      {dry ? <DryRunPanel data={dry} at={dateTime(dry.at)} /> : null}

      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      {query.isPending ? <Skeleton className="h-48" aria-busy="true" /> : null}
      <ul className="space-y-5">
        {(query.data ?? []).map((a) => (
          <li key={a.kind}>
            <AutomationCard automation={a} />
          </li>
        ))}
      </ul>
    </div>
  );
}

function DryRunPanel({ data, at }: { data: AutomationDryRun; at: string }) {
  const t = useTranslations("marketing.automations.dry");
  const tk = useTranslations("marketing.automations.kinds");
  const reasonLabel = useReasonLabel();
  return (
    <section aria-label={t("title")} className="space-y-3 rounded-card border border-line bg-card p-4 sm:p-5" data-testid="dry-run-panel">
      <h2 className="font-display text-lg font-semibold text-strong">{t("title")}</h2>
      <p className="text-sm text-muted">{t("hint", { at })}</p>
      {data.quietHoursNow ? <Alert variant="warning">{t("quietNow")}</Alert> : null}
      <ul className="grid gap-3 md:grid-cols-2">
        {data.automations.map((x) => (
          <li key={x.kind} className="rounded-control border border-line p-3" data-testid={`dry-${x.kind}`}>
            <p className="flex flex-wrap items-center gap-2 font-semibold text-strong">
              {tk(`${x.kind}.name`)}
              <Badge variant={x.isEnabled ? "success" : "outline"}>{x.isEnabled ? t("on") : t("off")}</Badge>
            </p>
            <dl className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
              <dt className="text-muted">{t("candidates")}</dt>
              <dd className="tabular-nums text-strong">{toNumber(x.candidates)}</dd>
              <dt className="text-muted">{t("wouldSend")}</dt>
              <dd className="tabular-nums text-strong" data-testid="dry-would-send">{toNumber(x.wouldSend)}</dd>
              <dt className="text-muted">{t("wouldSendIfEnabled")}</dt>
              <dd className="tabular-nums text-strong" data-testid="dry-would-send-if-enabled">{toNumber(x.wouldSendIfEnabled)}</dd>
              <dt className="text-muted">{t("deferred")}</dt>
              <dd className="tabular-nums text-strong">{toNumber(x.deferredByQuietHours)}</dd>
            </dl>
            {x.skipped.length > 0 ? (
              <ul className="mt-2 flex flex-wrap gap-2">
                {x.skipped.map((r) => (
                  <li key={r.reason}>
                    <Badge variant="neutral">
                      {reasonLabel(r.reason)}: {toNumber(r.count)}
                    </Badge>
                  </li>
                ))}
              </ul>
            ) : null}
          </li>
        ))}
      </ul>
    </section>
  );
}

function AutomationCard({ automation }: { automation: Automation }) {
  const t = useTranslations("marketing.automations");
  const tk = useTranslations("marketing.automations.kinds");
  const tch = useTranslations("marketing.channel");
  const td = useTranslations("marketing.automations.delay");
  const dateTime = useDateTime();
  const problemMessage = useProblemMessage();
  const perms = useMarketingPermissions();
  const update = useUpdateAutomation();
  const kind = automation.kind;
  const base = `auto-${kind}`;
  const [form, setForm] = React.useState<AutomationForm>(() => automationFormFrom(automation));
  const [baseline, setBaseline] = React.useState(() => JSON.stringify(automationFormFrom(automation)));
  const [errors, setErrors] = React.useState<AutomationErrors>({});
  const [serverErrors, setServerErrors] = React.useState<Record<string, string>>({});
  const [problem, setProblem] = React.useState<string | null>(null);
  const [confirmOn, setConfirmOn] = React.useState(false);
  const [showResults, setShowResults] = React.useState(false);
  const editable = perms.automationEdit;
  const dirty = JSON.stringify({ ...form, isEnabled: false }) !== JSON.stringify({ ...JSON.parse(baseline), isEnabled: false });
  const email = form.channel === "Email";
  const set = <K extends keyof AutomationForm>(key: K, value: AutomationForm[K]) => setForm((f) => ({ ...f, [key]: value }));
  const delay = Number.isFinite(Number(form.delayMinutes)) ? delayParts(Number(form.delayMinutes)) : null;

  const errorText = (field: keyof AutomationErrors) => (errors[field] ? t(`errors.${errors[field]}`) : serverErrors[field]);

  const toggle = async (on: boolean) => {
    try {
      const saved = await update.mutateAsync({ kind, body: { isEnabled: on } as never });
      setForm((f) => ({ ...f, isEnabled: saved.isEnabled }));
      setBaseline((b) => JSON.stringify({ ...JSON.parse(b), isEnabled: saved.isEnabled }));
      toast.success(on ? t("switchedOn", { name: tk(`${kind}.name`) }) : t("switchedOff", { name: tk(`${kind}.name`) }));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    }
    setConfirmOn(false);
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateAutomationForm(kind, form, automation.placeholders);
    setErrors(found);
    setServerErrors({});
    setProblem(null);
    if (Object.keys(found).length > 0) return;
    try {
      const { isEnabled: _ignored, ...patch } = automationPatch(kind, form);
      void _ignored;
      const saved = await update.mutateAsync({ kind, body: patch as never });
      const next = automationFormFrom(saved);
      setForm(next);
      setBaseline(JSON.stringify(next));
      toast.success(t("saved", { name: tk(`${kind}.name`) }));
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.errors) setServerErrors(fieldErrors(p));
      setProblem(problemMessage(p));
    }
  };

  return (
    <section aria-labelledby={`${base}-title`} className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5" data-testid={`automation-${kind}`}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 flex-1 basis-64">
          <h2 id={`${base}-title`} className="font-display text-lg font-semibold text-strong">
            {tk(`${kind}.name`)}
          </h2>
          <p className="text-sm text-muted">{tk(`${kind}.description`)}</p>
          <p className="mt-1 text-sm text-muted">
            {needsConsent(kind) ? t("needsConsent") : t("optOutHonoured")} · {t("lastRun", { when: automation.lastRunAt ? dateTime(automation.lastRunAt) : t("never") })}
          </p>
        </div>
        <ChoiceRow htmlFor={`${base}-switch`} className="shrink-0">
          <Switch
            id={`${base}-switch`}
            checked={automation.isEnabled}
            disabled={!editable || update.isPending}
            onCheckedChange={(on) => (on ? setConfirmOn(true) : void toggle(false))}
            data-testid={`switch-${kind}`}
          />
          <span className="font-medium" data-testid={`state-${kind}`}>
            {automation.isEnabled ? t("on") : t("off")}
          </span>
        </ChoiceRow>
      </div>

      <form onSubmit={submit} noValidate className="space-y-4">
        <fieldset disabled={!editable} className="min-w-0 space-y-4 border-0 p-0">
          {problem ? <Alert variant="danger">{problem}</Alert> : null}
          <div className="space-y-1">
            <p className="text-sm font-medium text-strong">{t("channel")}</p>
            <RadioGroup aria-label={`${tk(`${kind}.name`)}: ${t("channel")}`} value={form.channel} onValueChange={(v) => set("channel", v as MarketingChannel)} className="sm:grid-cols-2">
              {(["Sms", "Email"] as const).map((c) => (
                <ChoiceRow key={c} htmlFor={`${base}-ch-${c}`}>
                  <RadioItem value={c} id={`${base}-ch-${c}`} />
                  <span>{tch(c)}</span>
                </ChoiceRow>
              ))}
            </RadioGroup>
          </div>
          <div className="grid gap-4 sm:grid-cols-3">
            <Field label={t("delayLabel")} htmlFor={`${base}-delay`} required hint={`${td(DELAY_MEANING[kind])}${delay ? ` (${td(`unit.${delay.unit}`, { count: delay.count })})` : ""}`} error={errorText("delayMinutes")}>
              <Input id={`${base}-delay`} inputMode="numeric" value={form.delayMinutes} onChange={(e) => set("delayMinutes", e.target.value)} aria-invalid={!!errorText("delayMinutes")} />
            </Field>
            {usesWindow(kind) ? (
              <Field label={t("windowLabel")} htmlFor={`${base}-window`} required hint={t("windowHint")} error={errorText("windowDays")}>
                <Input id={`${base}-window`} inputMode="numeric" value={form.windowDays} onChange={(e) => set("windowDays", e.target.value)} aria-invalid={!!errorText("windowDays")} />
              </Field>
            ) : null}
            <Field label={t("capLabel")} htmlFor={`${base}-cap`} required hint={t("capHint")} error={errorText("capDays")}>
              <Input id={`${base}-cap`} inputMode="numeric" value={form.capDays} onChange={(e) => set("capDays", e.target.value)} aria-invalid={!!errorText("capDays")} />
            </Field>
          </div>
          {!usesWindow(kind) ? <p className="text-sm text-muted">{t("noWindow")}</p> : null}
          {email ? (
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label={t("subjectEn")} htmlFor={`${base}-subject-en`} required error={errorText("subjectEn")}>
                <Input id={`${base}-subject-en`} value={form.subjectEn} onChange={(e) => set("subjectEn", e.target.value)} aria-invalid={!!errorText("subjectEn")} />
              </Field>
              <Field label={t("subjectBn")} htmlFor={`${base}-subject-bn`}>
                <Input id={`${base}-subject-bn`} lang="bn" value={form.subjectBn} onChange={(e) => set("subjectBn", e.target.value)} />
              </Field>
            </div>
          ) : null}
          <div className="grid gap-5 lg:grid-cols-2">
            <MessageBox id={`${base}-body-en`} label={t("bodyEn")} required value={form.bodyEn} onChange={(v) => set("bodyEn", v)} error={errorText("bodyEn")} sms={!email} disabled={!editable} placeholders={automation.placeholders} />
            <MessageBox id={`${base}-body-bn`} label={t("bodyBn")} value={form.bodyBn} onChange={(v) => set("bodyBn", v)} error={serverErrors.bodyBn} sms={!email} disabled={!editable} placeholders={automation.placeholders} lang="bn" />
          </div>
        </fieldset>
        <div className="flex flex-wrap items-center gap-2">
          {editable ? (
            <Button type="submit" loading={update.isPending} disabled={!dirty} data-testid={`save-${kind}`}>
              {t("save")}
            </Button>
          ) : null}
          <Button type="button" variant="secondary" onClick={() => setShowResults((v) => !v)} aria-expanded={showResults} data-testid={`results-toggle-${kind}`}>
            {showResults ? t("hideResults") : t("showResults")}
          </Button>
        </div>
      </form>

      {showResults ? <AutomationResultsPanel kind={kind} /> : null}

      <ConfirmDialog
        open={confirmOn}
        onOpenChange={setConfirmOn}
        title={t("confirmOnTitle", { name: tk(`${kind}.name`) })}
        description={needsConsent(kind) ? t("confirmOnConsent") : t("confirmOnOptOut")}
        confirmLabel={t("confirmOn")}
        cancelLabel={t("keepOff")}
        closeLabel={t("close")}
        loading={update.isPending}
        onConfirm={() => toggle(true)}
      />
    </section>
  );
}

function AutomationResultsPanel({ kind }: { kind: Automation["kind"] }) {
  const t = useTranslations("marketing.automations.results");
  const money = useMoney();
  const dateTime = useDateTime();
  const query = useAutomationResults(kind);
  const r = query.data;
  if (query.isError) return <Alert variant="danger">{t("loadFailed")}</Alert>;
  if (!r) return <Skeleton className="h-20" aria-busy="true" />;
  return (
    <div className="space-y-3 rounded-control bg-sunken p-3" data-testid={`results-${kind}`}>
      <ul className="grid grid-cols-2 gap-2 md:grid-cols-4">
        <li><Stat label={t("queued")} value={toNumber(r.queued)} /></li>
        <li><Stat label={t("sent")} value={toNumber(r.sent)} /></li>
        <li><Stat label={t("failed")} value={toNumber(r.failed)} /></li>
        <li><Stat label={t("skipped")} value={toNumber(r.skipped)} /></li>
      </ul>
      <p className="text-strong">
        <span className="font-bold tabular-nums">{toNumber(r.attribution.orders)}</span> {t("orders")} · <span className="font-bold tabular-nums">{money(r.attribution.revenue)}</span> {t("revenue")}
      </p>
      <p className="text-sm text-muted">{t("attributionHint", { days: toNumber(r.attribution.windowDays) })}</p>
      <p className="text-sm text-muted">{t("lastRun", { when: r.lastRunAt ? dateTime(r.lastRunAt) : t("never") })}</p>
    </div>
  );
}
