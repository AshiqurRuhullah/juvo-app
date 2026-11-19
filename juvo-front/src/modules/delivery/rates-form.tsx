"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Lock, Plus, Trash2, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useMoney } from "@/modules/catalog/format";
import { useSaveRates, type Zone } from "./api";
import { fieldMessages } from "./common";
import {
  MAX_SLABS, formatWeight, hasRatesErrors, parseMoney, ratesToForm, ratesToRequest, ratesWarnings, sameRates, slabRanges, validateRates,
  type RatesErrorKey, type RatesErrors, type RatesForm,
} from "./logic";

/**
 * The rates of one zone: base fee, weight slabs, per-extra-kilogram fee, free-delivery threshold, remote surcharge and the heaviest parcel.
 * Money is typed in taka (two decimals at most), weights in kilograms; every value is checked before it is sent, and the table under the
 * slabs spells out the weight ranges the slabs make so a wrong limit is easy to see.
 */
export function RatesForm({ zone, canEdit, onDirty }: { zone: Zone; canEdit: boolean; onDirty: (dirty: boolean) => void }) {
  const t = useTranslations("delivery.rates");
  const te = useTranslations("delivery.rates.errors");
  const tw = useTranslations("delivery.rates.warnings");
  const tc = useTranslations("delivery.common");
  const money = useMoney();
  const problemMessage = useProblemMessage();
  const save = useSaveRates(zone.id);
  const saved = React.useMemo(() => ratesToForm(zone), [zone]);
  const [values, setValues] = React.useState<RatesForm>(saved);
  const [errors, setErrors] = React.useState<RatesErrors>({ slabRows: {} });
  const [failure, setFailure] = React.useState<{ text: string; lines: string[] } | null>(null);
  const dirty = !sameRates(values, saved);
  const international = zone.kind === "International";

  React.useEffect(() => onDirty(dirty), [dirty, onDirty]);

  const set = <K extends keyof RatesForm>(key: K, value: RatesForm[K]) => setValues((old) => ({ ...old, [key]: value }));
  const setSlab = (index: number, patch: Partial<RatesForm["slabs"][number]>) => set("slabs", values.slabs.map((s, i) => (i === index ? { ...s, ...patch } : s)));
  const err = (key: RatesErrorKey | undefined) => (key ? te(key) : undefined);
  const warnings = ratesWarnings(values);
  const ranges = slabRanges(values);
  const perKgParsed = parseMoney(values.perExtraKgFee);
  const perKg = perKgParsed.ok ? perKgParsed.value : 0;

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const found = validateRates(values);
    setErrors(found);
    if (hasRatesErrors(found)) return;
    try {
      const updated = await save.mutateAsync(ratesToRequest(values));
      setValues(ratesToForm(updated));
      toast.success(t("saved"));
    } catch (e) {
      const problem = e instanceof ApiError ? e.problem : null;
      setFailure({ text: problemMessage(problem), lines: fieldMessages(problem) });
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="space-y-4" data-testid="rates-form">
      {!canEdit ? (
        <Alert variant="info" data-testid="rates-read-only">
          <span className="flex items-start gap-2">
            <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("readOnly")}</span>
          </span>
        </Alert>
      ) : null}
      <Alert variant="info">{t("howItWorks")}</Alert>

      <Card>
        <CardHeader>
          <CardTitle>{t("feesTitle")}</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("baseFee")} htmlFor="rt-base" required hint={t("baseFeeHint")} error={err(errors.baseFee)}>
              <Input id="rt-base" inputMode="decimal" value={values.baseFee} disabled={!canEdit} onChange={(e) => set("baseFee", e.target.value)} aria-invalid={!!errors.baseFee} autoComplete="off" />
            </Field>
            <Field label={t("remoteSurcharge")} htmlFor="rt-remote" required hint={t("remoteHint")} error={err(errors.remoteSurcharge)}>
              <Input id="rt-remote" inputMode="decimal" value={values.remoteSurcharge} disabled={!canEdit} onChange={(e) => set("remoteSurcharge", e.target.value)} aria-invalid={!!errors.remoteSurcharge} autoComplete="off" />
            </Field>
            <Field label={t("freeThreshold")} htmlFor="rt-free" hint={t("freeHint")} error={err(errors.freeThreshold)}>
              <Input id="rt-free" inputMode="decimal" value={values.freeThreshold} disabled={!canEdit} onChange={(e) => set("freeThreshold", e.target.value)} aria-invalid={!!errors.freeThreshold} autoComplete="off" placeholder={t("none")} />
            </Field>
            <Field label={t("maxWeight")} htmlFor="rt-max" hint={international ? t("maxWeightHintIntl") : t("maxWeightHint")} error={err(errors.maxWeightKg)}>
              <Input id="rt-max" inputMode="decimal" value={values.maxWeightKg} disabled={!canEdit} onChange={(e) => set("maxWeightKg", e.target.value)} aria-invalid={!!errors.maxWeightKg} autoComplete="off" placeholder={t("noLimit")} />
            </Field>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("slabsTitle")}</CardTitle>
          <p className="text-sm text-muted">{t("slabsHelp")}</p>
        </CardHeader>
        <CardContent className="space-y-4">
          {values.slabs.length === 0 ? <p className="text-muted">{t("noSlabs")}</p> : null}
          {values.slabs.map((row, index) => {
            const rowErr = errors.slabRows[index] ?? {};
            return (
              <div key={index} className="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] items-start gap-2" data-testid="slab-row">
                <Field label={t("slabUpTo")} htmlFor={`rt-slab-kg-${index}`} error={err(rowErr.upToKg)}>
                  <Input id={`rt-slab-kg-${index}`} inputMode="decimal" value={row.upToKg} disabled={!canEdit} onChange={(e) => setSlab(index, { upToKg: e.target.value })} aria-invalid={!!rowErr.upToKg} autoComplete="off" />
                </Field>
                <Field label={t("slabFee")} htmlFor={`rt-slab-fee-${index}`} error={err(rowErr.extraFee)}>
                  <Input id={`rt-slab-fee-${index}`} inputMode="decimal" value={row.extraFee} disabled={!canEdit} onChange={(e) => setSlab(index, { extraFee: e.target.value })} aria-invalid={!!rowErr.extraFee} autoComplete="off" />
                </Field>
                {canEdit ? (
                  <Button type="button" variant="ghost" size="icon" className="mt-6" aria-label={t("removeSlab", { n: index + 1 })} onClick={() => set("slabs", values.slabs.filter((_, i) => i !== index))}>
                    <Trash2 aria-hidden />
                  </Button>
                ) : (
                  <span />
                )}
              </div>
            );
          })}
          {errors.slabs ? (
            <p role="alert" className="text-sm font-medium text-danger">
              {te(errors.slabs, { max: MAX_SLABS })}
            </p>
          ) : null}
          {canEdit ? (
            <Button type="button" variant="secondary" disabled={values.slabs.length >= MAX_SLABS} onClick={() => set("slabs", [...values.slabs, { upToKg: "", extraFee: "" }])} data-testid="slab-add">
              <Plus aria-hidden /> {t("addSlab")}
            </Button>
          ) : null}
          <Field label={t("perExtraKg")} htmlFor="rt-perkg" required hint={t("perExtraKgHint")} error={err(errors.perExtraKgFee)}>
            <Input id="rt-perkg" inputMode="decimal" value={values.perExtraKgFee} disabled={!canEdit} onChange={(e) => set("perExtraKgFee", e.target.value)} aria-invalid={!!errors.perExtraKgFee} autoComplete="off" className="max-w-xs" />
          </Field>
        </CardContent>
      </Card>

      {ranges.length > 0 ? (
        <Card>
          <CardHeader>
            <CardTitle>{t("rangesTitle")}</CardTitle>
            <p className="text-sm text-muted">{t("rangesHelp")}</p>
          </CardHeader>
          <CardContent>
            <ul className="divide-y divide-line rounded-card border border-line" data-testid="slab-ranges">
              {ranges.map((r, i) => (
                <li key={i} className="flex flex-wrap items-center justify-between gap-2 px-4 py-3">
                  <span className="font-medium text-strong">{r.beyond ? t("rangeAbove", { from: formatWeight(r.fromGrams) }) : t("rangeBetween", { from: formatWeight(r.fromGrams), to: formatWeight(r.toGrams ?? 0) })}</span>
                  <span className="text-sm text-muted">{r.beyond ? t("rangeBeyondFee", { perKg: money(perKg) }) : t("rangeFee", { fee: money(r.fee ?? 0) })}</span>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
      ) : null}

      {warnings.length > 0 ? (
        <Alert variant="warning" title={t("warningsTitle")} data-testid="rates-warnings">
          <ul className="list-disc space-y-1 ps-5">
            {warnings.map((w, i) => (
              <li key={i}>{tw(w.code, { row: "row" in w ? w.row + 1 : 0 })}</li>
            ))}
          </ul>
        </Alert>
      ) : null}

      {failure ? (
        <Alert variant="danger" data-testid="rates-form-error">
          <span className="flex items-start gap-2">
            <TriangleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>
              {failure.text}
              {failure.lines.map((l) => (
                <span key={l} className="block">
                  {l}
                </span>
              ))}
            </span>
          </span>
        </Alert>
      ) : null}

      {canEdit ? (
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:items-center sm:justify-end">
          {dirty ? <Badge variant="warning">{tc("unsaved")}</Badge> : null}
          <Button type="button" variant="secondary" disabled={!dirty || save.isPending} onClick={() => { setValues(saved); setErrors({ slabRows: {} }); setFailure(null); }}>
            {tc("discard")}
          </Button>
          <Button type="submit" loading={save.isPending} data-testid="rates-save">
            {t("save")}
          </Button>
        </div>
      ) : null}
    </form>
  );
}
