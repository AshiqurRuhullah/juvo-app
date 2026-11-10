"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Field, Input, Label, Switch, Textarea } from "@/components/ui/form";
import { Select, type SelectOption } from "@/components/ui/select";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { TARGET_GROUPS } from "./api";
import type { DraftErrors, ProductDraft } from "./product-draft";

export type FormOptions = {
  categories: SelectOption[];
  brands: SelectOption[];
  sizeCharts: SelectOption[];
};

type Change = <K extends keyof ProductDraft>(key: K, value: ProductDraft[K]) => void;

type Props = { draft: ProductDraft; errors: DraftErrors; onChange: Change; disabled?: boolean };

const NONE = "__none";

/** A text field wired to the draft: label, hint, inline error and the right keyboard on phones. */
function TextField({
  id, label, value, onChange, error, hint, required, disabled, inputMode, maxLength, autoComplete = "off", multiline,
}: {
  id: string; label: string; value: string; onChange: (value: string) => void; error?: string; hint?: string; required?: boolean; disabled?: boolean;
  inputMode?: "text" | "decimal" | "numeric" | "url"; maxLength?: number; autoComplete?: string; multiline?: boolean;
}) {
  const describedBy = error ? `${id}-error` : hint ? `${id}-hint` : undefined;
  return (
    <Field label={label} htmlFor={id} hint={hint} error={error} required={required}>
      {multiline ? (
        <Textarea id={id} value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled} maxLength={maxLength} aria-invalid={!!error} aria-describedby={describedBy} />
      ) : (
        <Input id={id} value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled} inputMode={inputMode} maxLength={maxLength} autoComplete={autoComplete} aria-invalid={!!error} aria-describedby={describedBy} />
      )}
    </Field>
  );
}

function SwitchRow({ id, label, hint, checked, onChange, disabled }: { id: string; label: string; hint: string; checked: boolean; onChange: (value: boolean) => void; disabled?: boolean }) {
  return (
    <div className="flex items-start justify-between gap-4 py-1">
      <Label htmlFor={id} className="block min-w-0 cursor-pointer py-2">
        {label}
        <span className="block text-sm font-normal text-muted">{hint}</span>
      </Label>
      <Switch id={id} checked={checked} onCheckedChange={onChange} disabled={disabled} className="mt-1" />
    </div>
  );
}

/** Name, description, category, price, labels, delivery details (FR-B01, FR-B03, FR-B12). */
export function ProductDetailsForm({ draft, errors, onChange, disabled, options, canSeeCost }: Props & { options: FormOptions; canSeeCost: boolean }) {
  const t = useTranslations("catalog.form");
  const tg = useTranslations("catalog.targetGroups");

  return (
    <div className="space-y-5">
      <Card>
        <CardHeader>
          <CardTitle>{t("basics")}</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <TextField id="p-name" label={t("nameEn")} value={draft.nameEn} onChange={(v) => onChange("nameEn", v)} error={errors.nameEn} required disabled={disabled} maxLength={200} />
          <TextField id="p-name-bn" label={t("nameBn")} value={draft.nameBn} onChange={(v) => onChange("nameBn", v)} error={errors.nameBn} disabled={disabled} maxLength={200} />
          <Field label={t("category")} htmlFor="p-category" error={errors.categoryId} required>
            <Select
              id="p-category"
              value={draft.categoryId}
              onValueChange={(v) => onChange("categoryId", v)}
              options={options.categories}
              placeholder={t("categoryPlaceholder")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              disabled={disabled}
              invalid={!!errors.categoryId}
            />
          </Field>
          <Field label={t("brand")} htmlFor="p-brand">
            <Select
              id="p-brand"
              value={draft.brandId || NONE}
              onValueChange={(v) => onChange("brandId", v === NONE ? "" : v)}
              options={[{ value: NONE, label: t("noBrand") }, ...options.brands]}
              placeholder={t("brandPlaceholder")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              disabled={disabled}
            />
          </Field>
          <Field label={t("targetGroup")} htmlFor="p-target">
            <Select
              id="p-target"
              value={draft.targetGroup}
              onValueChange={(v) => onChange("targetGroup", v as ProductDraft["targetGroup"])}
              options={TARGET_GROUPS.map((g) => ({ value: g, label: tg(g) }))}
              placeholder={t("targetGroup")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              disabled={disabled}
            />
          </Field>
          <TextField id="p-slug" label={t("slug")} hint={t("slugHint")} value={draft.slug} onChange={(v) => onChange("slug", v)} error={errors.slug} disabled={disabled} maxLength={150} />
          <div className="sm:col-span-2">
            <TextField id="p-short" label={t("shortDescription")} hint={t("shortDescriptionHint")} value={draft.shortDescription} onChange={(v) => onChange("shortDescription", v)} disabled={disabled} maxLength={500} />
          </div>
          <div className="sm:col-span-2">
            <TextField id="p-description" label={t("description")} value={draft.description} onChange={(v) => onChange("description", v)} disabled={disabled} maxLength={8000} multiline />
          </div>
          <div className="sm:col-span-2">
            <TextField id="p-tags" label={t("tags")} hint={t("tagsHint")} value={draft.tags} onChange={(v) => onChange("tags", v)} disabled={disabled} maxLength={500} />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("pricing")}</CardTitle>
          <CardDescription>{t("pricingHint")}</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <TextField id="p-price" label={t("regularPrice")} value={draft.regularPrice} onChange={(v) => onChange("regularPrice", v)} error={errors.regularPrice} required inputMode="decimal" disabled={disabled} />
          <TextField id="p-sale" label={t("salePrice")} hint={t("salePriceHint")} value={draft.salePrice} onChange={(v) => onChange("salePrice", v)} error={errors.salePrice} inputMode="decimal" disabled={disabled} />
          <Field label={t("saleStart")} htmlFor="p-sale-start" error={errors.saleStart}>
            <Input id="p-sale-start" type="date" value={draft.saleStart} onChange={(e) => onChange("saleStart", e.target.value)} disabled={disabled} />
          </Field>
          <Field label={t("saleEnd")} htmlFor="p-sale-end" error={errors.saleEnd}>
            <Input id="p-sale-end" type="date" value={draft.saleEnd} min={draft.saleStart || undefined} onChange={(e) => onChange("saleEnd", e.target.value)} disabled={disabled} aria-invalid={!!errors.saleEnd} />
          </Field>
          {canSeeCost ? (
            <TextField id="p-cost" label={t("costPrice")} hint={t("costPriceHint")} value={draft.costPrice} onChange={(v) => onChange("costPrice", v)} error={errors.costPrice} inputMode="decimal" disabled={disabled} />
          ) : null}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("labels")}</CardTitle>
          <CardDescription>{t("labelsHint")}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-2">
          <SwitchRow id="p-new" label={t("isNew")} hint={t("isNewHint")} checked={draft.isNew} onChange={(v) => onChange("isNew", v)} disabled={disabled} />
          <SwitchRow id="p-featured" label={t("isFeatured")} hint={t("isFeaturedHint")} checked={draft.isFeatured} onChange={(v) => onChange("isFeatured", v)} disabled={disabled} />
          <SwitchRow id="p-best" label={t("isBestSeller")} hint={t("isBestSellerHint")} checked={draft.isBestSeller} onChange={(v) => onChange("isBestSeller", v)} disabled={disabled} />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t("delivery")}</CardTitle>
          <CardDescription>{t("deliveryHint")}</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 sm:grid-cols-2">
          <TextField id="p-weight" label={t("weightGrams")} value={draft.weightGrams} onChange={(v) => onChange("weightGrams", v)} error={errors.weightGrams} inputMode="numeric" disabled={disabled} />
          <Field label={t("sizeChart")} htmlFor="p-chart" hint={t("sizeChartHint")}>
            <Select
              id="p-chart"
              value={draft.sizeChartId || NONE}
              onValueChange={(v) => onChange("sizeChartId", v === NONE ? "" : v)}
              options={[{ value: NONE, label: t("noSizeChart") }, ...options.sizeCharts]}
              placeholder={t("sizeChart")}
              searchPlaceholder={t("search")}
              emptyText={t("noMatch")}
              disabled={disabled}
            />
          </Field>
          <TextField id="p-length" label={t("lengthCm")} value={draft.lengthCm} onChange={(v) => onChange("lengthCm", v)} error={errors.lengthCm} inputMode="decimal" disabled={disabled} />
          <TextField id="p-width" label={t("widthCm")} value={draft.widthCm} onChange={(v) => onChange("widthCm", v)} error={errors.widthCm} inputMode="decimal" disabled={disabled} />
          <TextField id="p-height" label={t("heightCm")} value={draft.heightCm} onChange={(v) => onChange("heightCm", v)} error={errors.heightCm} inputMode="decimal" disabled={disabled} />
          <TextField id="p-material" label={t("material")} value={draft.material} onChange={(v) => onChange("material", v)} disabled={disabled} maxLength={200} />
          <div className="sm:col-span-2">
            <TextField id="p-care" label={t("care")} value={draft.care} onChange={(v) => onChange("care", v)} disabled={disabled} maxLength={500} />
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

/** Search-engine and sharing texts, and the optional video (FR-B13, FR-B07). */
export function ProductSeoForm({ draft, errors, onChange, disabled }: Props) {
  const t = useTranslations("catalog.form");
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("seo")}</CardTitle>
        <CardDescription>{t("seoHint")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        <TextField id="p-meta-title" label={t("metaTitle")} hint={t("counter", { count: draft.metaTitle.length, max: 120 })} value={draft.metaTitle} onChange={(v) => onChange("metaTitle", v)} error={errors.metaTitle} disabled={disabled} />
        <TextField id="p-meta-description" label={t("metaDescription")} hint={t("counter", { count: draft.metaDescription.length, max: 320 })} value={draft.metaDescription} onChange={(v) => onChange("metaDescription", v)} error={errors.metaDescription} disabled={disabled} multiline />
        <TextField id="p-video" label={t("videoUrl")} hint={t("videoUrlHint")} value={draft.videoUrl} onChange={(v) => onChange("videoUrl", v)} error={errors.videoUrl} inputMode="url" disabled={disabled} maxLength={500} />
      </CardContent>
    </Card>
  );
}
