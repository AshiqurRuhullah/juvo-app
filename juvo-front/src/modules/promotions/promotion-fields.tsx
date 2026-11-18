"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ImageUp } from "lucide-react";
import { Checkbox, ChoiceRow, Field, Input, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { mediaUrl } from "@/lib/media";
import { useCategoryTree } from "@/modules/catalog/api";
import { flattenCategories } from "@/modules/catalog/category-options";
import { useDeliveryZones } from "@/modules/couriers/zones";
import { useUploadPromotionImage, type PromotionDetail } from "./api";
import { useTypeOptions } from "./common";
import { TargetsEditor } from "./target-picker";
import {
  BANNER_MAX_BYTES,
  BANNER_MIN_SIDE,
  BANNER_SLOTS,
  CHANNELS,
  DISCOUNT_KINDS,
  STACK_MODES,
  TYPE_RULES,
  parseZoneCodes,
  stackingText,
  type Channel,
  type PromotionForm,
} from "./logic";

export type Errors = Record<string, string | undefined>;
type SetField = <K extends keyof PromotionForm>(key: K, value: PromotionForm[K]) => void;
type Props = { form: PromotionForm; set: SetField; errors: Errors; isNew: boolean; detail?: PromotionDetail };

const inputProps = (errors: Errors, key: string) => ({ "aria-invalid": errors[key] ? true : undefined, "aria-describedby": errors[key] ? `${key}-error` : undefined });

/** Name, type and the customer label. */
export function BasicsSection({ form, set, errors, isNew }: Props) {
  const t = useTranslations("promotions.editor");
  const tt = useTranslations("promotions.type");
  const tc = useTranslations("promotions.common");
  const typeOptions = useTypeOptions();
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("basics.title")}</CardTitle>
        <CardDescription>{t("basics.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4 md:grid-cols-2">
        <Field label={t("basics.type")} htmlFor="pf-type" hint={isNew ? tt(`${form.type}Hint`) : t("basics.typeLocked")} className="md:col-span-2">
          <Select id="pf-type" value={form.type} onValueChange={(v) => set("type", v as PromotionForm["type"])} options={typeOptions} placeholder={t("basics.type")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} disabled={!isNew} />
        </Field>
        <Field label={t("basics.nameEn")} htmlFor="nameEn" required error={errors.nameEn}>
          <Input id="nameEn" value={form.nameEn} maxLength={150} onChange={(e) => set("nameEn", e.target.value)} autoComplete="off" {...inputProps(errors, "nameEn")} />
        </Field>
        <Field label={t("basics.nameBn")} htmlFor="nameBn" error={errors.nameBn}>
          <Input id="nameBn" value={form.nameBn} maxLength={150} onChange={(e) => set("nameBn", e.target.value)} autoComplete="off" lang="bn" />
        </Field>
        <Field label={t("basics.labelEn")} htmlFor="labelEn" hint={t("basics.labelHint")}>
          <Input id="labelEn" value={form.labelEn} maxLength={60} onChange={(e) => set("labelEn", e.target.value)} autoComplete="off" />
        </Field>
        <Field label={t("basics.labelBn")} htmlFor="labelBn">
          <Input id="labelBn" value={form.labelBn} maxLength={60} onChange={(e) => set("labelBn", e.target.value)} autoComplete="off" lang="bn" />
        </Field>
      </CardContent>
    </Card>
  );
}

/** The offer itself: the fields that count for the chosen type. */
export function OfferSection({ form, set, errors }: Props) {
  const t = useTranslations("promotions.editor");
  const td = useTranslations("promotions.discountKind");
  const tc = useTranslations("promotions.common");
  const rule = TYPE_RULES[form.type];
  const kindOptions = DISCOUNT_KINDS.map((k) => ({ value: k, label: td(k) }));
  const showDiscount = rule.discount !== false;
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("offer.title")}</CardTitle>
        <CardDescription>{t(`offer.description.${form.type}`)}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4 md:grid-cols-2">
        {showDiscount ? (
          <>
            <Field label={t("offer.discountKind")} htmlFor="pf-kind" hint={td(`${form.discountKind}Hint`)}>
              <Select id="pf-kind" value={form.discountKind} onValueChange={(v) => set("discountKind", v as PromotionForm["discountKind"])} options={kindOptions} placeholder={t("offer.discountKind")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
            </Field>
            <Field label={form.discountKind === "Percent" ? t("offer.valuePercent") : t("offer.valueAmount")} htmlFor="discountValue" required={rule.discount === "required"} error={errors.discountValue} hint={rule.discount === "optional" ? t("offer.optionalDiscount") : undefined}>
              <Input id="discountValue" inputMode="decimal" value={form.discountValue} onChange={(e) => set("discountValue", e.target.value)} {...inputProps(errors, "discountValue")} />
            </Field>
            {form.discountKind === "Percent" ? (
              <Field label={t("offer.maxDiscount")} htmlFor="maxDiscount" hint={t("offer.maxDiscountHint")} error={errors.maxDiscount}>
                <Input id="maxDiscount" inputMode="decimal" value={form.maxDiscount} onChange={(e) => set("maxDiscount", e.target.value)} {...inputProps(errors, "maxDiscount")} />
              </Field>
            ) : null}
          </>
        ) : null}
        {rule.bundle ? (
          <Field label={t("offer.bundlePrice")} htmlFor="bundlePrice" required error={errors.bundlePrice} hint={t("offer.bundlePriceHint")}>
            <Input id="bundlePrice" inputMode="decimal" value={form.bundlePrice} onChange={(e) => set("bundlePrice", e.target.value)} {...inputProps(errors, "bundlePrice")} />
          </Field>
        ) : null}
        {rule.buyXGetY ? (
          <>
            <Field label={t("offer.buyQuantity")} htmlFor="buyQuantity" required error={errors.buyQuantity}>
              <Input id="buyQuantity" inputMode="numeric" value={form.buyQuantity} onChange={(e) => set("buyQuantity", e.target.value.replace(/[^0-9]/g, ""))} {...inputProps(errors, "buyQuantity")} />
            </Field>
            <Field label={t("offer.getQuantity")} htmlFor="getQuantity" required error={errors.getQuantity}>
              <Input id="getQuantity" inputMode="numeric" value={form.getQuantity} onChange={(e) => set("getQuantity", e.target.value.replace(/[^0-9]/g, ""))} {...inputProps(errors, "getQuantity")} />
            </Field>
            <Field label={t("offer.getPercent")} htmlFor="getDiscountPercent" required error={errors.getDiscountPercent} hint={t("offer.getPercentHint")}>
              <Input id="getDiscountPercent" inputMode="decimal" value={form.getDiscountPercent} onChange={(e) => set("getDiscountPercent", e.target.value)} {...inputProps(errors, "getDiscountPercent")} />
            </Field>
          </>
        ) : null}
        {rule.freeDelivery ? (
          <>
            <Field label={t("offer.freeFrom")} htmlFor="minCartValue" required error={errors.minCartValue} hint={t("offer.freeFromHint")}>
              <Input id="minCartValue" inputMode="decimal" value={form.minCartValue} onChange={(e) => set("minCartValue", e.target.value)} {...inputProps(errors, "minCartValue")} />
            </Field>
            <ZonePicker form={form} set={set} error={errors.zoneCodes} />
          </>
        ) : null}
      </CardContent>
    </Card>
  );
}

function ZonePicker({ form, set, error }: { form: PromotionForm; set: SetField; error?: string }) {
  const t = useTranslations("promotions.editor.offer");
  const zones = useDeliveryZones(true);
  const chosen = parseZoneCodes(form.zoneCodes);
  if (zones.isPending) return <Skeleton className="h-12" aria-busy="true" />;
  if (zones.isError || !zones.data) {
    return (
      <Field label={t("zonesTyped")} htmlFor="zoneCodes" hint={t("zonesTypedHint")} error={error}>
        <Input id="zoneCodes" value={form.zoneCodes} onChange={(e) => set("zoneCodes", e.target.value)} autoComplete="off" />
      </Field>
    );
  }
  const toggle = (code: string, on: boolean) => set("zoneCodes", (on ? [...new Set([...chosen, code])] : chosen.filter((c) => c !== code)).join(", "));
  return (
    <fieldset className="min-w-0 md:col-span-2" data-testid="zone-picker">
      <legend className="mb-1.5 text-sm font-semibold text-strong">{t("zones")}</legend>
      <p className="mb-2 text-sm text-muted">{chosen.length === 0 ? t("zonesAll") : t("zonesSome", { count: chosen.length })}</p>
      <div className="grid gap-x-4 sm:grid-cols-2 xl:grid-cols-3">
        {zones.data
          .filter((z) => z.isActive)
          .map((z) => (
            <ChoiceRow key={z.id} htmlFor={`zone-${z.code}`}>
              <Checkbox id={`zone-${z.code}`} checked={chosen.includes(z.code)} onCheckedChange={(v) => toggle(z.code, v === true)} />
              <span>
                {z.nameEn} <span className="font-mono text-xs text-muted">{z.code}</span>
              </span>
            </ChoiceRow>
          ))}
      </div>
      {error ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {error}
        </p>
      ) : null}
    </fieldset>
  );
}

/** What the promotion applies to. */
export function TargetsSection({ form, set, errors }: Props) {
  const t = useTranslations("promotions.editor");
  const rule = TYPE_RULES[form.type];
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("targets.title")}</CardTitle>
        <CardDescription>{t(`targets.description.${form.type}`)}</CardDescription>
      </CardHeader>
      <CardContent>
        <TargetsEditor form={form} rule={rule} error={errors.targets} onChange={(targets) => set("targets", targets)} />
      </CardContent>
    </Card>
  );
}

/** Schedule (typed as Dhaka time), limits, channels and the tie-break priority. */
export function ScheduleSection({ form, set, errors }: Props) {
  const t = useTranslations("promotions.editor");
  const tch = useTranslations("promotions.channel");
  const rule = TYPE_RULES[form.type];
  const channels = form.channels;
  const toggle = (c: Channel, on: boolean) => set("channels", CHANNELS.filter((x) => (x === c ? on : channels.includes(x))));
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("schedule.title")}</CardTitle>
        <CardDescription>{t("schedule.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4 md:grid-cols-2">
        <Field label={t("schedule.startsAt")} htmlFor="startsAt" hint={t("schedule.startsHint")} error={errors.startsAt}>
          <Input id="startsAt" type="datetime-local" value={form.startsAt} onChange={(e) => set("startsAt", e.target.value)} {...inputProps(errors, "startsAt")} />
        </Field>
        <Field label={t("schedule.endsAt")} htmlFor="endsAt" required={rule.endsRequired} hint={rule.endsRequired ? t("schedule.endsRequiredHint") : t("schedule.endsHint")} error={errors.endsAt}>
          <Input id="endsAt" type="datetime-local" value={form.endsAt} min={form.startsAt || undefined} onChange={(e) => set("endsAt", e.target.value)} {...inputProps(errors, "endsAt")} />
        </Field>
        <p className="text-sm text-muted md:col-span-2" data-testid="dhaka-note">
          {t("schedule.timezone")}
        </p>
        <Field label={t("schedule.usageLimit")} htmlFor="usageLimit" hint={t("schedule.usageLimitHint")} error={errors.usageLimit}>
          <Input id="usageLimit" inputMode="numeric" value={form.usageLimit} onChange={(e) => set("usageLimit", e.target.value.replace(/[^0-9]/g, ""))} {...inputProps(errors, "usageLimit")} />
        </Field>
        {!rule.freeDelivery ? (
          <Field label={t("schedule.minCartValue")} htmlFor="minCartValue" hint={t("schedule.minCartHint")} error={errors.minCartValue}>
            <Input id="minCartValue" inputMode="decimal" value={form.minCartValue} onChange={(e) => set("minCartValue", e.target.value)} {...inputProps(errors, "minCartValue")} />
          </Field>
        ) : null}
        <Field label={t("schedule.priority")} htmlFor="priority" hint={t("schedule.priorityHint")} error={errors.priority}>
          <Input id="priority" inputMode="numeric" value={form.priority} onChange={(e) => set("priority", e.target.value.replace(/(?!^-)[^0-9]/g, ""))} {...inputProps(errors, "priority")} />
        </Field>
        <fieldset className="min-w-0">
          <legend className="mb-1.5 text-sm font-semibold text-strong">{t("schedule.channels")}</legend>
          <div className="flex flex-wrap gap-x-5">
            {CHANNELS.map((c) => (
              <ChoiceRow key={c} htmlFor={`channel-${c}`}>
                <Checkbox id={`channel-${c}`} checked={channels.includes(c)} onCheckedChange={(v) => toggle(c, v === true)} />
                {tch(c)}
              </ChoiceRow>
            ))}
          </div>
          <p className="text-sm text-muted">{t("schedule.channelsHint")}</p>
          {errors.channels ? (
            <p role="alert" className="text-sm font-medium text-danger">
              {errors.channels}
            </p>
          ) : null}
        </fieldset>
      </CardContent>
    </Card>
  );
}

/** The stack mode with the plain-language explanation of the policy (docs/pricing-engine.md section 3). */
export function StackingSection({ form, set }: Props) {
  const t = useTranslations("promotions.stacking");
  const tm = useTranslations("promotions.stackMode");
  const text = stackingText(form.type, form.stackMode);
  const delivery = TYPE_RULES[form.type].level === "delivery";
  return (
    <Card data-testid="stacking-section">
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("intro")}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <RadioGroup value={form.stackMode} onValueChange={(v) => set("stackMode", v as PromotionForm["stackMode"])} aria-label={t("title")} disabled={delivery}>
          {STACK_MODES.map((m) => (
            <ChoiceRow key={m} htmlFor={`stack-${m}`} className="items-start py-1">
              <RadioItem id={`stack-${m}`} value={m} className="mt-2" />
              <span>
                <span className="block font-medium">{tm(m)}</span>
                <span className="block text-sm text-muted">{tm(`${m}Hint`)}</span>
              </span>
            </ChoiceRow>
          ))}
        </RadioGroup>
        <Alert variant="info" title={t(`headline.${text.headline}`)} data-testid="stacking-explanation">
          <ul className="list-disc space-y-1 ps-5">
            {text.points.map((p) => (
              <li key={p}>{t(`points.${p}`)}</li>
            ))}
          </ul>
        </Alert>
        <p className="text-sm text-muted">{t("sameForAll")}</p>
      </CardContent>
    </Card>
  );
}

/** Festival campaign page: address, titles, texts, banner placement and pictures. */
export function CampaignSection({ form, set, errors, detail }: Props) {
  const t = useTranslations("promotions.editor");
  const tb = useTranslations("promotions.bannerSlot");
  const tc = useTranslations("promotions.common");
  const tree = useCategoryTree(false);
  const categories = React.useMemo(() => flattenCategories(tree.data ?? []).map((c) => ({ value: c.id, label: c.label })), [tree.data]);
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("campaign.title")}</CardTitle>
        <CardDescription>{t("campaign.description")}</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4 md:grid-cols-2">
        <Field label={t("campaign.slug")} htmlFor="slug" required error={errors.slug} hint={t("campaign.slugHint", { slug: form.slug.trim() || "eid-2026" })} className="md:col-span-2">
          <Input id="slug" value={form.slug} maxLength={100} onChange={(e) => set("slug", e.target.value.toLowerCase().replace(/[^a-z0-9-]/g, ""))} autoComplete="off" spellCheck={false} {...inputProps(errors, "slug")} />
        </Field>
        <Field label={t("campaign.titleEn")} htmlFor="titleEn" required error={errors.titleEn}>
          <Input id="titleEn" value={form.titleEn} maxLength={200} onChange={(e) => set("titleEn", e.target.value)} {...inputProps(errors, "titleEn")} />
        </Field>
        <Field label={t("campaign.titleBn")} htmlFor="titleBn">
          <Input id="titleBn" value={form.titleBn} maxLength={200} onChange={(e) => set("titleBn", e.target.value)} lang="bn" />
        </Field>
        <Field label={t("campaign.bodyEn")} htmlFor="bodyEn">
          <Textarea id="bodyEn" value={form.bodyEn} maxLength={8000} onChange={(e) => set("bodyEn", e.target.value)} className="min-h-28" />
        </Field>
        <Field label={t("campaign.bodyBn")} htmlFor="bodyBn">
          <Textarea id="bodyBn" value={form.bodyBn} maxLength={8000} onChange={(e) => set("bodyBn", e.target.value)} className="min-h-28" lang="bn" />
        </Field>
        <Field label={t("campaign.bannerSlot")} htmlFor="pf-slot" hint={t("campaign.bannerSlotHint")}>
          <Select id="pf-slot" value={form.bannerSlot} onValueChange={(v) => set("bannerSlot", v as PromotionForm["bannerSlot"])} options={BANNER_SLOTS.map((s) => ({ value: s, label: tb(s) }))} placeholder={t("campaign.bannerSlot")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        {form.bannerSlot === "Category" ? (
          <Field label={t("campaign.bannerCategory")} htmlFor="pf-bannercat" required error={errors.bannerCategoryId}>
            <Select id="pf-bannercat" value={form.bannerCategoryId} onValueChange={(v) => set("bannerCategoryId", v)} options={categories} placeholder={t("campaign.bannerCategory")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.bannerCategoryId} />
          </Field>
        ) : null}
        <div className="space-y-4 md:col-span-2">
          {detail ? <ImageUploads detail={detail} /> : <Alert variant="info">{t("campaign.imagesAfterSave")}</Alert>}
        </div>
      </CardContent>
    </Card>
  );
}

/** The longest side of a picture file, read in the browser, so a too small file is refused before it is sent (the API checks again). */
function imageSide(file: File): Promise<number> {
  return new Promise((resolve) => {
    const url = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      URL.revokeObjectURL(url);
      resolve(Math.max(img.naturalWidth, img.naturalHeight));
    };
    img.onerror = () => {
      URL.revokeObjectURL(url);
      resolve(0);
    };
    img.src = url;
  });
}

function ImageUploads({ detail }: { detail: PromotionDetail }) {
  const t = useTranslations("promotions.images");
  const problem = useProblemMessage();
  const upload = useUploadPromotionImage(detail.id);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState<"hero" | "banner" | null>(null);
  const archived = detail.isArchived;

  const pick = async (kind: "hero" | "banner", file: File | undefined) => {
    if (!file) return;
    setError(null);
    if (!/^image\/(jpeg|png|webp)$/.test(file.type)) return setError(t("badType"));
    if (file.size > BANNER_MAX_BYTES) return setError(t("tooBig"));
    if ((await imageSide(file)) < BANNER_MIN_SIDE) return setError(t("tooSmall", { px: BANNER_MIN_SIDE }));
    setBusy(kind);
    try {
      await upload.mutateAsync({ kind, file });
      toast.success(t("uploaded"));
    } catch (e) {
      setError(e instanceof ApiError && e.problem.status === 400 ? t("rejected") : problem(e instanceof ApiError ? e.problem : null));
    } finally {
      setBusy(null);
    }
  };

  const slots: { kind: "hero" | "banner"; url: string | null }[] = [
    { kind: "hero", url: detail.heroImageUrl },
    { kind: "banner", url: detail.bannerImageUrl },
  ];
  return (
    <div className="space-y-3" data-testid="promotion-images">
      <p className="text-sm text-muted">{t("rules", { px: BANNER_MIN_SIDE })}</p>
      <div className="grid gap-4 md:grid-cols-2">
        {slots.map((s) => {
          const src = mediaUrl(s.url);
          return (
            <div key={s.kind} className="space-y-2 rounded-card border border-line p-3" data-testid={`image-${s.kind}`}>
              <p className="font-semibold text-strong">{t(s.kind)}</p>
              <p className="text-sm text-muted">{t(`${s.kind}Hint`)}</p>
              {src ? (
                // eslint-disable-next-line @next/next/no-img-element -- a stored picture served by the same-origin media route
                <img src={src} alt={t("alt", { kind: t(s.kind) })} className="aspect-[16/7] w-full rounded-control border border-line object-cover" data-testid={`image-${s.kind}-preview`} />
              ) : (
                <div className="flex aspect-[16/7] w-full items-center justify-center rounded-control border border-dashed border-line text-sm text-muted">{t("none")}</div>
              )}
              {!archived ? (
                <label className="inline-flex min-h-11 cursor-pointer items-center gap-2 rounded-control border border-line bg-card px-4 font-medium text-strong focus-within:ring-2 focus-within:ring-primary [@media(hover:hover)]:hover:bg-sunken">
                  <ImageUp className="size-5" aria-hidden />
                  {busy === s.kind ? t("uploading") : src ? t("replace") : t("choose")}
                  <input type="file" accept="image/jpeg,image/png,image/webp" className="sr-only" data-testid={`upload-${s.kind}`} disabled={busy !== null} onChange={(e) => { void pick(s.kind, e.target.files?.[0]); e.target.value = ""; }} />
                </label>
              ) : null}
            </div>
          );
        })}
      </div>
      {error ? <Alert variant="danger" data-testid="image-error">{error}</Alert> : null}
    </div>
  );
}
