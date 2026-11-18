"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, RadioGroup, RadioItem, ChoiceRow, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { toNumber } from "@/modules/catalog/format";
import { useSegmentCount, useSegmentOptions } from "./api";
import { TextCounter } from "./common";
import { CAMPAIGN_PLACEHOLDERS, NAME_MAX, MARKETING_CHANNELS, unknownPlaceholders, type CampaignErrors, type CampaignForm, type MarketingChannel } from "./logic";

/**
 * The campaign form: name, segment (with its live size), channel, and the message in English and Bengali with placeholder buttons and
 * a character counter (for SMS also the number of parts). WhatsApp is not offered: the API has no WhatsApp campaigns.
 */
export function CampaignFormFields({
  form,
  onChange,
  errors,
  serverErrors,
  disabled,
  channelLocked,
}: {
  form: CampaignForm;
  onChange: (next: CampaignForm) => void;
  errors: CampaignErrors;
  serverErrors: Record<string, string>;
  disabled?: boolean;
  channelLocked?: boolean;
}) {
  const t = useTranslations("marketing.campaigns.form");
  const tc = useTranslations("marketing.common");
  const tch = useTranslations("marketing.channel");
  const segments = useSegmentOptions();
  const count = useSegmentCount(form.segmentId || null);
  const set = <K extends keyof CampaignForm>(key: K, value: CampaignForm[K]) => onChange({ ...form, [key]: value });
  const email = form.channel === "Email";
  const err = (key: keyof CampaignErrors, serverKey?: string) => (errors[key] ? t(`errors.${errors[key]}`, { max: NAME_MAX }) : serverErrors[serverKey ?? key]);

  return (
    <div className="space-y-5">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("name")} htmlFor="cmp-name" required error={err("name")}>
          <Input id="cmp-name" value={form.name} maxLength={NAME_MAX} disabled={disabled} onChange={(e) => set("name", e.target.value)} aria-invalid={!!err("name")} />
        </Field>
        <Field label={t("segment")} htmlFor="cmp-segment" required error={err("segmentId")}>
          <Select
            id="cmp-segment"
            value={form.segmentId}
            onValueChange={(v) => set("segmentId", v)}
            options={(segments.data?.items ?? []).map((s) => ({ value: s.id, label: s.name }))}
            placeholder={t("pickSegment")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
            disabled={disabled}
            invalid={!!err("segmentId")}
          />
        </Field>
      </div>

      {form.segmentId ? (
        <p className="text-sm text-muted" aria-live="polite" data-testid="campaign-segment-count">
          {count.data ? t("segmentCount", { total: toNumber(count.data.total), sms: toNumber(count.data.reachable.sms), email: toNumber(count.data.reachable.email) }) : count.isError ? t("segmentCountFailed") : t("segmentCountLoading")}
        </p>
      ) : null}

      <div className="space-y-1">
        <p className="text-sm font-medium text-strong">{t("channel")}</p>
        <RadioGroup aria-label={t("channel")} value={form.channel} onValueChange={(v) => set("channel", v as MarketingChannel)} disabled={disabled || channelLocked} className="sm:grid-cols-2">
          {MARKETING_CHANNELS.map((c) => (
            <ChoiceRow key={c} htmlFor={`cmp-ch-${c}`}>
              <RadioItem value={c} id={`cmp-ch-${c}`} />
              <span>{tch(c)}</span>
            </ChoiceRow>
          ))}
        </RadioGroup>
        <p className="text-sm text-muted">{t("channelHint")}</p>
      </div>

      {email ? (
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("subjectEn")} htmlFor="cmp-subject-en" required error={err("subjectEn")}>
            <Input id="cmp-subject-en" value={form.subjectEn} disabled={disabled} onChange={(e) => set("subjectEn", e.target.value)} aria-invalid={!!err("subjectEn")} />
          </Field>
          <Field label={t("subjectBn")} htmlFor="cmp-subject-bn" hint={t("subjectBnHint")} error={serverErrors.subjectBn}>
            <Input id="cmp-subject-bn" lang="bn" value={form.subjectBn} disabled={disabled} onChange={(e) => set("subjectBn", e.target.value)} />
          </Field>
        </div>
      ) : null}

      <div className="grid gap-5 lg:grid-cols-2">
        <MessageBox id="cmp-body-en" label={t("bodyEn")} required value={form.bodyEn} onChange={(v) => set("bodyEn", v)} error={err("bodyEn")} sms={!email} disabled={disabled} />
        <MessageBox id="cmp-body-bn" label={t("bodyBn")} hint={t("bodyBnHint")} value={form.bodyBn} onChange={(v) => set("bodyBn", v)} error={serverErrors.bodyBn} sms={!email} disabled={disabled} lang="bn" />
      </div>
      {unknownPlaceholders(form.bodyEn + " " + form.bodyBn + " " + form.subjectEn + " " + form.subjectBn).length > 0 ? (
        <Alert variant="warning">{t("unknownPlaceholders", { names: unknownPlaceholders(form.bodyEn + " " + form.bodyBn + " " + form.subjectEn + " " + form.subjectBn).map((p) => `{${p}}`).join(", ") })}</Alert>
      ) : null}
      <p className="text-sm text-muted">{email ? t("emailFooter") : t("smsFooter")}</p>
    </div>
  );
}

/** A text box with buttons that insert a placeholder at the cursor, and the counter. */
export function MessageBox({ id, label, hint, required, value, onChange, error, sms, disabled, lang, placeholders = CAMPAIGN_PLACEHOLDERS }: { id: string; label: string; hint?: string; required?: boolean; value: string; onChange: (value: string) => void; error?: string; sms: boolean; disabled?: boolean; lang?: string; placeholders?: readonly string[] }) {
  const t = useTranslations("marketing.campaigns.form");
  const ref = React.useRef<HTMLTextAreaElement>(null);
  const insert = (name: string) => {
    const el = ref.current;
    const token = `{${name}}`;
    const start = el?.selectionStart ?? value.length;
    const end = el?.selectionEnd ?? value.length;
    onChange(value.slice(0, start) + token + value.slice(end));
    requestAnimationFrame(() => {
      el?.focus();
      el?.setSelectionRange(start + token.length, start + token.length);
    });
  };
  return (
    <div className="space-y-2">
      <Field label={label} htmlFor={id} required={required} hint={hint} error={error}>
        <Textarea ref={ref} id={id} lang={lang} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} className="min-h-36" aria-invalid={!!error} aria-describedby={`${id}-counter`} />
      </Field>
      <TextCounter id={`${id}-counter`} text={value} sms={sms} />
      {!disabled ? (
        <div className="flex flex-wrap items-center gap-2" role="group" aria-label={t("placeholders")}>
          <span className="text-sm text-muted">{t("insert")}</span>
          {placeholders.map((p) => (
            <Button key={p} type="button" variant="secondary" className="min-h-11 px-3 text-sm" onClick={() => insert(p)} aria-label={t("insertPlaceholder", { name: p })}>
              {`{${p}}`}
            </Button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
