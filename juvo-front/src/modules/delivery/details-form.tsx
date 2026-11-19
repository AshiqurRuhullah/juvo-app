"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Lock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, CardContent } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useCreateZone, useUpdateZone, type Zone } from "./api";
import { fieldMessages } from "./common";
import {
  detailsToCreateRequest, detailsToForm, detailsToUpdateRequest, emptyDetailsForm, validateDetails,
  type DetailsErrors, type DetailsForm, type ZoneKind,
} from "./logic";

/** Name, code, type, status, order and delivery days of a zone. New zones start here; rates and areas are set on the zone's own page afterwards. */
export function DetailsFormCard({ zone, canEdit, onDirty }: { zone: Zone | null; canEdit: boolean; onDirty: (dirty: boolean) => void }) {
  const t = useTranslations("delivery.details");
  const te = useTranslations("delivery.details.errors");
  const tc = useTranslations("delivery.common");
  const tk = useTranslations("delivery.kind");
  const router = useRouter();
  const problemMessage = useProblemMessage();
  const create = useCreateZone();
  const update = useUpdateZone(zone?.id ?? "");
  const isNew = !zone;
  const saved = React.useMemo(() => (zone ? detailsToForm(zone) : emptyDetailsForm()), [zone]);
  const [values, setValues] = React.useState<DetailsForm>(saved);
  const [errors, setErrors] = React.useState<DetailsErrors>({});
  const [failure, setFailure] = React.useState<{ text: string; lines: string[] } | null>(null);
  const dirty = !isNew && JSON.stringify(values) !== JSON.stringify(saved);
  const pending = create.isPending || update.isPending;

  React.useEffect(() => onDirty(dirty), [dirty, onDirty]);

  const set = <K extends keyof DetailsForm>(key: K, value: DetailsForm[K]) => setValues((old) => ({ ...old, [key]: value }));
  const err = (key: keyof DetailsErrors) => (errors[key] ? te(errors[key]!) : undefined);
  const domestic = values.kind === "Domestic";

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const found = validateDetails(values, isNew);
    setErrors(found);
    if (Object.keys(found).length) return;
    try {
      if (isNew) {
        const made = await create.mutateAsync(detailsToCreateRequest(values));
        toast.success(t("created"));
        router.push(`/admin/delivery/zones/${made.id}`);
      } else {
        await update.mutateAsync(detailsToUpdateRequest(values));
        toast.success(t("saved"));
      }
    } catch (e) {
      const problem = e instanceof ApiError ? e.problem : null;
      setFailure({ text: problemMessage(problem), lines: fieldMessages(problem) });
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate data-testid="details-form">
      <Card>
        <CardContent className="space-y-4 pt-5">
          {!canEdit ? (
            <Alert variant="info" data-testid="details-read-only">
              <span className="flex items-start gap-2">
                <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
                <span>{t("readOnly")}</span>
              </span>
            </Alert>
          ) : null}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("code")} htmlFor="zd-code" required={isNew} hint={isNew ? t("codeHint") : t("codeFixed")} error={err("code")}>
              <Input id="zd-code" value={values.code} maxLength={30} disabled={!isNew} onChange={(e) => set("code", e.target.value.toUpperCase())} aria-invalid={!!errors.code} autoComplete="off" />
            </Field>
            <Field label={t("kind")} htmlFor="zd-kind" hint={isNew ? t("kindHint") : t("kindFixed")}>
              {isNew ? (
                <Select
                  id="zd-kind"
                  value={values.kind}
                  onValueChange={(v) => set("kind", v as ZoneKind)}
                  options={(["Domestic", "International"] as const).map((k) => ({ value: k, label: tk(k) }))}
                  placeholder={t("kind")}
                  searchPlaceholder={tc("search")}
                  emptyText={tc("noMatch")}
                />
              ) : (
                <Input id="zd-kind" value={tk(values.kind)} disabled readOnly />
              )}
            </Field>
            <Field label={t("nameEn")} htmlFor="zd-name" required error={err("nameEn")}>
              <Input id="zd-name" value={values.nameEn} maxLength={110} disabled={!canEdit} onChange={(e) => set("nameEn", e.target.value)} aria-invalid={!!errors.nameEn} autoComplete="off" />
            </Field>
            <Field label={t("nameBn")} htmlFor="zd-name-bn" error={err("nameBn")}>
              <Input id="zd-name-bn" value={values.nameBn} maxLength={110} disabled={!canEdit} onChange={(e) => set("nameBn", e.target.value)} aria-invalid={!!errors.nameBn} autoComplete="off" />
            </Field>
            <Field label={t("daysMin")} htmlFor="zd-dmin" required hint={t("daysHint")} error={err("daysMin")}>
              <Input id="zd-dmin" inputMode="numeric" value={values.daysMin} disabled={!canEdit} onChange={(e) => set("daysMin", e.target.value)} aria-invalid={!!errors.daysMin} autoComplete="off" />
            </Field>
            <Field label={t("daysMax")} htmlFor="zd-dmax" required error={err("daysMax")}>
              <Input id="zd-dmax" inputMode="numeric" value={values.daysMax} disabled={!canEdit} onChange={(e) => set("daysMax", e.target.value)} aria-invalid={!!errors.daysMax} autoComplete="off" />
            </Field>
            <Field label={t("sortOrder")} htmlFor="zd-sort" hint={t("sortHint")} error={err("sortOrder")}>
              <Input id="zd-sort" inputMode="numeric" value={values.sortOrder} disabled={!canEdit} onChange={(e) => set("sortOrder", e.target.value)} aria-invalid={!!errors.sortOrder} autoComplete="off" />
            </Field>
          </div>
          <div className="space-y-1">
            <ChoiceRow htmlFor="zd-active">
              <Switch id="zd-active" checked={values.isActive} disabled={!canEdit} onCheckedChange={(v) => set("isActive", v)} />
              {t("active")}
            </ChoiceRow>
            <p className="text-sm text-muted">{t("activeHint")}</p>
          </div>
          {domestic ? (
            <div className="space-y-1">
              <ChoiceRow htmlFor="zd-fallback">
                <Switch id="zd-fallback" checked={values.isFallback} disabled={!canEdit} onCheckedChange={(v) => set("isFallback", v)} />
                {t("fallback")}
              </ChoiceRow>
              <p className="text-sm text-muted">{t("fallbackHint")}</p>
            </div>
          ) : null}
          {!isNew && zone && zone.kind === "International" ? (
            <p className="text-sm text-muted" data-testid="zone-countries">
              {zone.countryCodes.length ? t("usedByCountries", { codes: zone.countryCodes.join(", ") }) : t("noCountriesYet")}
            </p>
          ) : null}
          {failure ? (
            <Alert variant="danger" data-testid="details-form-error">
              {failure.text}
              {failure.lines.map((l) => (
                <span key={l} className="block">
                  {l}
                </span>
              ))}
            </Alert>
          ) : null}
          {canEdit ? (
            <div className="flex flex-col-reverse gap-2 sm:flex-row sm:items-center sm:justify-end">
              {dirty ? <Badge variant="warning">{tc("unsaved")}</Badge> : null}
              {dirty ? (
                <Button type="button" variant="secondary" disabled={pending} onClick={() => { setValues(saved); setErrors({}); setFailure(null); }}>
                  {tc("discard")}
                </Button>
              ) : null}
              <Button type="submit" loading={pending} data-testid="details-save">
                {isNew ? t("create") : t("save")}
              </Button>
            </div>
          ) : null}
        </CardContent>
      </Card>
    </form>
  );
}
