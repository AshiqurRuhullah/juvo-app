"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CalendarClock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, Switch } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber } from "@/modules/catalog/format";
import { usePreorderSettings, useSaveProductPreorder, useSaveVariantPreorder, type PreorderSettings, type VariantPreorder } from "./api";
import { dhakaToday, draftFromSettings, draftToRequest, sameDraft, validatePreorderDraft, type PreorderDraft, type PreorderErrors } from "./logic";

function useErrorText() {
  const t = useTranslations("preorders.settings.errors");
  return (errors: PreorderErrors) => ({
    date: errors.date ? t(errors.date === "past" ? "datePast" : "dateInvalid") : undefined,
    percent: errors.percent ? t(errors.percent === "range" ? "percentRange" : "percentInvalid") : undefined,
  });
}

/** Date and advance percent inputs shared by the product block and each variant row. */
function DateAndAdvance({ idPrefix, draft, onChange, errors, disabled, hintDate, hintPercent }: { idPrefix: string; draft: PreorderDraft; onChange: (next: PreorderDraft) => void; errors: { date?: string; percent?: string }; disabled: boolean; hintDate?: string; hintPercent?: string }) {
  const t = useTranslations("preorders.settings");
  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <Field label={t("shipDate")} htmlFor={`${idPrefix}-date`} hint={hintDate ?? t("shipDateHint")} error={errors.date}>
        <Input id={`${idPrefix}-date`} type="date" value={draft.date} min={dhakaToday()} disabled={disabled} aria-invalid={!!errors.date} onChange={(e) => onChange({ ...draft, date: e.target.value })} />
      </Field>
      <Field label={t("advance")} htmlFor={`${idPrefix}-percent`} hint={hintPercent ?? t("advanceHint")} error={errors.percent}>
        <Input
          id={`${idPrefix}-percent`}
          type="number"
          inputMode="decimal"
          min={0}
          max={100}
          step="any"
          value={draft.percent}
          disabled={disabled}
          aria-invalid={!!errors.percent}
          onChange={(e) => onChange({ ...draft, percent: e.target.value })}
        />
      </Field>
    </div>
  );
}

/** Product level: the switch, the expected ship date and the advance percent. */
function ProductBlock({ productId, settings, disabled }: { productId: string; settings: PreorderSettings; disabled: boolean }) {
  const t = useTranslations("preorders.settings");
  const save = useSaveProductPreorder(productId);
  const problem = useProblemMessage();
  const errorText = useErrorText();
  const saved = React.useMemo(() => draftFromSettings(settings, false), [settings]);
  const [draft, setDraft] = React.useState<PreorderDraft>(saved);
  const [serverErrors, setServerErrors] = React.useState<{ date?: string; percent?: string }>({});
  const [showErrors, setShowErrors] = React.useState(false);
  const checks = validatePreorderDraft(draft, dhakaToday());
  const errors = showErrors ? { ...errorText(checks), ...serverErrors } : serverErrors;

  const submit = async () => {
    setShowErrors(true);
    setServerErrors({});
    if (checks.date || checks.percent) return;
    try {
      await save.mutateAsync(draftToRequest(draft));
      toast.success(t("saved"));
      setShowErrors(false);
    } catch (failure) {
      if (failure instanceof ApiError) {
        const found = fieldErrors(failure.problem);
        setServerErrors({ date: found.expectedShipDate, percent: found.advancePercent });
        toast.error(problem(failure.problem));
      } else toast.error(problem(null));
    }
  };

  return (
    <Card data-testid="preorder-product">
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-lg">
          <CalendarClock className="size-5 text-muted" aria-hidden /> {t("productTitle")}
        </CardTitle>
        <CardDescription>{t("productHelp")}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="flex min-h-11 items-center justify-between gap-3">
          <label htmlFor="pre-allow" className="min-w-0 flex-1 cursor-pointer font-medium text-strong">
            {t("allow")}
            <span className="block text-sm font-normal text-muted">{t("allowHint")}</span>
          </label>
          <Switch id="pre-allow" checked={draft.allow === "yes"} disabled={disabled} onCheckedChange={(on) => setDraft({ ...draft, allow: on ? "yes" : "no" })} />
        </div>
        <DateAndAdvance idPrefix="pre-product" draft={draft} onChange={setDraft} errors={errors} disabled={disabled} />
        {!disabled ? (
          <div className="flex justify-end gap-2">
            <Button variant="secondary" disabled={sameDraft(draft, saved) || save.isPending} onClick={() => { setDraft(saved); setShowErrors(false); setServerErrors({}); }}>
              {t("discard")}
            </Button>
            <Button loading={save.isPending} disabled={sameDraft(draft, saved)} onClick={() => void submit()} data-testid="preorder-save">
              {t("save")}
            </Button>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}

/** One variant: inherit the product, force on or force off, with its own date and advance. */
function VariantRow({ productId, variant, disabled }: { productId: string; variant: VariantPreorder; disabled: boolean }) {
  const t = useTranslations("preorders.settings");
  const save = useSaveVariantPreorder(productId);
  const problem = useProblemMessage();
  const errorText = useErrorText();
  const saved = React.useMemo(() => draftFromSettings(variant, true), [variant]);
  const [draft, setDraft] = React.useState<PreorderDraft>(saved);
  const [showErrors, setShowErrors] = React.useState(false);
  const [serverErrors, setServerErrors] = React.useState<{ date?: string; percent?: string }>({});
  const checks = validatePreorderDraft(draft, dhakaToday());
  const errors = showErrors ? { ...errorText(checks), ...serverErrors } : serverErrors;
  const label = [variant.sku, variant.colorName, variant.size].filter(Boolean).join(" · ");
  const effectiveDate = variant.effectiveShipDate ?? null;

  const submit = async () => {
    setShowErrors(true);
    setServerErrors({});
    if (checks.date || checks.percent) return;
    try {
      await save.mutateAsync({ variantId: variant.variantId, body: draftToRequest(draft) });
      toast.success(t("variantSaved", { sku: variant.sku }));
      setShowErrors(false);
    } catch (failure) {
      if (failure instanceof ApiError) {
        const found = fieldErrors(failure.problem);
        setServerErrors({ date: found.expectedShipDate, percent: found.advancePercent });
        toast.error(problem(failure.problem));
      } else toast.error(problem(null));
    }
  };

  const id = `pre-v-${variant.variantId}`;
  return (
    <li className="space-y-3 rounded-card border border-line bg-card p-4" data-testid="preorder-variant" data-sku={variant.sku}>
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <p className="font-medium text-strong [overflow-wrap:anywhere]">{label}</p>
        <p className="text-sm text-muted" data-testid="preorder-effective">
          {variant.effectiveAllow
            ? t("effectiveOn", { date: effectiveDate ? effectiveDate.slice(0, 10) : t("noDate"), percent: toNumber(variant.effectiveAdvancePercent) })
            : t("effectiveOff")}
        </p>
      </div>
      <Field label={t("variantAllow")} htmlFor={id}>
        <Select
          id={id}
          value={draft.allow}
          disabled={disabled}
          onValueChange={(v) => setDraft({ ...draft, allow: v as PreorderDraft["allow"] })}
          options={[
            { value: "inherit", label: t("inherit") },
            { value: "yes", label: t("yes") },
            { value: "no", label: t("no") },
          ]}
          placeholder={t("variantAllow")}
          searchPlaceholder=""
          emptyText=""
        />
      </Field>
      <DateAndAdvance idPrefix={id} draft={draft} onChange={setDraft} errors={errors} disabled={disabled} hintDate={t("variantDateHint")} hintPercent={t("variantAdvanceHint")} />
      {!disabled ? (
        <div className="flex justify-end">
          <Button variant="secondary" loading={save.isPending} disabled={sameDraft(draft, saved)} onClick={() => void submit()} aria-label={t("saveVariant", { sku: variant.sku })}>
            {t("save")}
          </Button>
        </div>
      ) : null}
    </li>
  );
}

/** The product editor's "Pre-order" tab (FR-AA01): the product switch with its date and advance, and each variant's override. */
export function PreorderSettingsPanel({ productId, disabled }: { productId: string; disabled: boolean }) {
  const t = useTranslations("preorders.settings");
  const query = usePreorderSettings(productId);
  const problem = useProblemMessage();
  if (query.isLoading) return <Skeleton className="h-64" aria-busy />;
  if (query.isError || !query.data) return <Alert variant="danger">{problem(query.error instanceof ApiError ? query.error.problem : null)}</Alert>;
  const data = query.data;
  // Fields are keyed by the saved values, so a save (or another editor's change) refills the form from the server copy.
  const productKey = `${data.allowPreorder}|${data.expectedShipDate}|${data.advancePercent}`;
  return (
    <div className="space-y-5">
      <ProductBlock key={productKey} productId={productId} settings={data} disabled={disabled} />
      {data.variants.length > 0 ? (
        <section aria-labelledby="pre-variants" className="space-y-3">
          <div>
            <h2 id="pre-variants" className="text-lg font-semibold text-strong">
              {t("variantsTitle")}
            </h2>
            <p className="text-sm text-muted">{t("variantsHelp")}</p>
          </div>
          <ul className="space-y-3" aria-label={t("variantsTitle")}>
            {data.variants.map((v) => (
              <VariantRow key={`${v.variantId}|${v.allowPreorder}|${v.expectedShipDate}|${v.advancePercent}`} productId={productId} variant={v} disabled={disabled} />
            ))}
          </ul>
        </section>
      ) : null}
      <Alert variant="info">{t("rule")}</Alert>
    </div>
  );
}
