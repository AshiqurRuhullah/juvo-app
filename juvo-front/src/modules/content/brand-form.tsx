"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useBrand, useSaveBrand, type BrandProfile } from "./api";
import { FailureAlert, ReadOnlyNote, useContentProblem, useContentRights } from "./common";
import { brandToForm, brandToRequest, hasErrors, validateBrand, type BrandForm } from "./logic";

/** Brand details shown on the shop: name, address, phone, WhatsApp, e-mail, hours and an optional map point. */
export function BrandView() {
  const brand = useBrand();
  const problem = useContentProblem();
  if (brand.isPending) return <Skeleton className="h-96 w-full" aria-busy="true" />;
  if (brand.isError) return <Alert variant="danger">{problem(brand.error instanceof ApiError ? brand.error.problem : null).text}</Alert>;
  return <BrandBody key={brand.data.updatedAt} brand={brand.data} />;
}

function BrandBody({ brand }: { brand: BrandProfile }) {
  const t = useTranslations("content.brand");
  const te = useTranslations("content.brand.errors");
  const tc = useTranslations("content.common");
  const rights = useContentRights();
  const problem = useContentProblem();
  const save = useSaveBrand();
  const saved = React.useMemo(() => brandToForm(brand), [brand]);
  const [form, setForm] = React.useState<BrandForm>(saved);
  const [errors, setErrors] = React.useState<ReturnType<typeof validateBrand>>({});
  const [failure, setFailure] = React.useState<{ text: string; lines: string[] } | null>(null);
  const dirty = JSON.stringify(form) !== JSON.stringify(saved);
  const canEdit = rights.brandEdit;
  const set = (key: keyof BrandForm, value: string) => setForm((old) => ({ ...old, [key]: value }));
  const err = (key: keyof BrandForm) => {
    const code = errors[key];
    return code ? te(code, { max: key.startsWith("address") ? 500 : key.startsWith("working") ? 300 : 120 }) : undefined;
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const found = validateBrand(form);
    setErrors(found);
    if (hasErrors(found)) return;
    try {
      await save.mutateAsync(brandToRequest(form, brand.logoFileRef));
      toast.success(t("saved"));
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="space-y-5" data-testid="brand-form">
      <p className="max-w-3xl text-muted">{t("subtitle")}</p>
      {!canEdit ? <ReadOnlyNote testId="brand-read-only">{t("readOnly")}</ReadOnlyNote> : null}

      <Card>
        <CardHeader>
          <CardTitle>{t("identity")}</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <Field label={t("businessName")} htmlFor="br-name" required error={err("businessName")}>
            <Input id="br-name" value={form.businessName} maxLength={130} disabled={!canEdit} onChange={(e) => set("businessName", e.target.value)} autoComplete="off" aria-invalid={!!errors.businessName} />
          </Field>
          <div className="space-y-1.5" data-testid="brand-logo">
            <p className="text-sm font-medium text-strong">{t("logo")}</p>
            <p className="rounded-control border border-line bg-sunken px-3 py-2.5 text-sm text-strong [overflow-wrap:anywhere]">{brand.logoFileRef ?? t("logoNone")}</p>
            <p className="text-sm text-muted">{t("logoNote")}</p>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("contact")}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <Field label={t("phone")} htmlFor="br-phone" hint={t("phoneHint")} error={err("phone")}>
            <Input id="br-phone" type="tel" inputMode="tel" value={form.phone} disabled={!canEdit} onChange={(e) => set("phone", e.target.value)} autoComplete="off" aria-invalid={!!errors.phone} />
          </Field>
          <Field label={t("whatsApp")} htmlFor="br-wa" hint={t("whatsAppHint")} error={err("whatsApp")}>
            <Input id="br-wa" type="tel" inputMode="tel" value={form.whatsApp} disabled={!canEdit} onChange={(e) => set("whatsApp", e.target.value)} autoComplete="off" />
          </Field>
          <Field label={t("email")} htmlFor="br-email" error={err("email")} className="sm:col-span-2">
            <Input id="br-email" type="email" inputMode="email" value={form.email} disabled={!canEdit} onChange={(e) => set("email", e.target.value)} autoComplete="off" aria-invalid={!!errors.email} />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("addressAndHours")}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <Field label={t("addressEn")} htmlFor="br-addr-en" error={err("addressEn")}>
            <Textarea id="br-addr-en" className="min-h-24" value={form.addressEn} disabled={!canEdit} onChange={(e) => set("addressEn", e.target.value)} aria-invalid={!!errors.addressEn} />
          </Field>
          <Field label={t("addressBn")} htmlFor="br-addr-bn" error={err("addressBn")}>
            <Textarea id="br-addr-bn" className="min-h-24" lang="bn" value={form.addressBn} disabled={!canEdit} onChange={(e) => set("addressBn", e.target.value)} aria-invalid={!!errors.addressBn} />
          </Field>
          <Field label={t("hoursEn")} htmlFor="br-hours-en" hint={t("hoursHint")} error={err("workingHoursEn")}>
            <Textarea id="br-hours-en" className="min-h-20" value={form.workingHoursEn} disabled={!canEdit} onChange={(e) => set("workingHoursEn", e.target.value)} aria-invalid={!!errors.workingHoursEn} />
          </Field>
          <Field label={t("hoursBn")} htmlFor="br-hours-bn" error={err("workingHoursBn")}>
            <Textarea id="br-hours-bn" className="min-h-20" lang="bn" value={form.workingHoursBn} disabled={!canEdit} onChange={(e) => set("workingHoursBn", e.target.value)} aria-invalid={!!errors.workingHoursBn} />
          </Field>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("map")}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <p className="text-sm text-muted sm:col-span-2">{t("mapHint")}</p>
          <Field label={t("latitude")} htmlFor="br-lat" error={err("latitude")}>
            <Input id="br-lat" inputMode="decimal" value={form.latitude} disabled={!canEdit} onChange={(e) => set("latitude", e.target.value)} autoComplete="off" aria-invalid={!!errors.latitude} />
          </Field>
          <Field label={t("longitude")} htmlFor="br-lng" error={err("longitude")}>
            <Input id="br-lng" inputMode="decimal" value={form.longitude} disabled={!canEdit} onChange={(e) => set("longitude", e.target.value)} autoComplete="off" aria-invalid={!!errors.longitude} />
          </Field>
        </CardContent>
      </Card>

      <FailureAlert failure={failure} testId="brand-error" />

      {canEdit ? (
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:items-center sm:justify-end">
          {dirty ? <Badge variant="warning">{tc("unsaved")}</Badge> : null}
          {dirty ? (
            <Button type="button" variant="secondary" disabled={save.isPending} onClick={() => { setForm(saved); setErrors({}); setFailure(null); }}>
              {tc("discard")}
            </Button>
          ) : null}
          <Button type="submit" loading={save.isPending} disabled={!dirty} data-testid="brand-save">
            {t("save")}
          </Button>
        </div>
      ) : null}
    </form>
  );
}
