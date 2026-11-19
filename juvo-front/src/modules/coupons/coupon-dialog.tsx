"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, RadioGroup, RadioItem, Switch, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useSaveCoupon, type Coupon } from "./api";
import { useCouponProblem } from "./common";
import {
  CODE_MAX, MAX_DESCRIPTION, couponToForm, couponToRequest, emptyCouponForm, exampleDiscount, generateCode, hasCouponErrors, normaliseCode, validateCoupon,
  type CouponErrors, type CouponForm, type CouponKind,
} from "./logic";

const EXAMPLE_BASKET = 2000;

/** Create or edit one coupon. The code is fixed once saved (customers may already hold it). */
export function CouponDialog({ target, onClose }: { target: Coupon | "new" | null; onClose: () => void }) {
  const t = useTranslations("coupons.form");
  const tc = useTranslations("coupons.common");
  return (
    <Dialog open={target !== null} onOpenChange={(open) => (open ? undefined : onClose())}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{target === "new" || target === null ? t("newTitle") : t("editTitle", { code: target.code })}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{target === "new" || target === null ? t("newDescription") : t("editDescription")}</DialogDescription>
        {target !== null ? <CouponBody coupon={target === "new" ? null : target} close={onClose} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function CouponBody({ coupon, close }: { coupon: Coupon | null; close: () => void }) {
  const t = useTranslations("coupons.form");
  const te = useTranslations("coupons.form.errors");
  const tc = useTranslations("coupons.common");
  const money = useMoney();
  const problem = useCouponProblem();
  const save = useSaveCoupon();
  const isNew = !coupon;
  const usedCount = coupon ? toNumber(coupon.usedCount) : 0;
  const [values, setValues] = React.useState<CouponForm>(() => (coupon ? couponToForm(coupon) : emptyCouponForm()));
  const [errors, setErrors] = React.useState<CouponErrors>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const [taken, setTaken] = React.useState(false);
  const set = <K extends keyof CouponForm>(key: K, value: CouponForm[K]) => setValues((old) => ({ ...old, [key]: value }));
  const err = (key: keyof CouponErrors) => (errors[key] ? te(errors[key]!) : undefined);
  const percent = values.kind === "Percent";
  const example = exampleDiscount(values, EXAMPLE_BASKET);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateCoupon(values, { isNew, usedCount });
    setErrors(found);
    setFailure(null);
    setTaken(false);
    if (hasCouponErrors(found)) return;
    try {
      await save.mutateAsync({ id: coupon?.id, body: couponToRequest(values) });
      toast.success(isNew ? t("created", { code: normaliseCode(values.code) }) : t("saved", { code: coupon.code }));
      close();
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.code === "COUPON.CODE_TAKEN") setTaken(true);
      else setFailure(problem(p));
    }
  };

  const kinds: { kind: CouponKind; id: string }[] = [
    { kind: "Percent", id: "cp-kind-percent" },
    { kind: "Fixed", id: "cp-kind-fixed" },
  ];

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-5" data-testid="coupon-form">
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("code")} htmlFor="cp-code" required hint={isNew ? t("codeHint") : t("codeLocked")} error={taken ? t("codeTaken") : err("code")}>
          <div className="flex gap-2">
            <Input
              id="cp-code"
              value={values.code}
              maxLength={CODE_MAX}
              disabled={!isNew}
              onChange={(e) => {
                setTaken(false);
                set("code", normaliseCode(e.target.value));
              }}
              aria-invalid={!!errors.code || taken}
              autoCapitalize="characters"
              autoComplete="off"
              spellCheck={false}
              className="font-mono"
            />
            {isNew ? (
              <Button type="button" variant="secondary" onClick={() => set("code", generateCode())} data-testid="coupon-generate" className="shrink-0">
                <Sparkles aria-hidden /> {t("generate")}
              </Button>
            ) : null}
          </div>
        </Field>
        <Field label={t("description")} htmlFor="cp-description" hint={t("descriptionHint", { max: MAX_DESCRIPTION })} error={err("description")}>
          <Textarea id="cp-description" value={values.description} rows={2} maxLength={MAX_DESCRIPTION + 50} onChange={(e) => set("description", e.target.value)} aria-invalid={!!errors.description} />
        </Field>
      </div>

      <fieldset className="space-y-1">
        <legend className="text-sm font-medium text-strong">{t("kind")}</legend>
        <RadioGroup value={values.kind} onValueChange={(v) => set("kind", v as CouponKind)} aria-label={t("kind")}>
          {kinds.map(({ kind, id }) => (
            <ChoiceRow key={kind} htmlFor={id} className="items-start py-2">
              <RadioItem id={id} value={kind} data-testid={`kind-${kind}`} className="mt-0.5" />
              <span>
                <span className="block font-medium">{t(`kinds.${kind}`)}</span>
                <span className="block text-sm text-muted">{t(`kinds.${kind}Hint`)}</span>
              </span>
            </ChoiceRow>
          ))}
        </RadioGroup>
      </fieldset>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={percent ? t("valuePercent") : t("valueAmount")} htmlFor="cp-value" required hint={percent ? t("valuePercentHint") : t("valueAmountHint")} error={err("value")}>
          <Input id="cp-value" inputMode="decimal" value={values.value} onChange={(e) => set("value", e.target.value)} aria-invalid={!!errors.value} autoComplete="off" />
        </Field>
        {percent ? (
          <Field label={t("cap")} htmlFor="cp-cap" hint={t("capHint")} error={err("maxDiscount")}>
            <Input id="cp-cap" inputMode="decimal" value={values.maxDiscount} onChange={(e) => set("maxDiscount", e.target.value)} aria-invalid={!!errors.maxDiscount} autoComplete="off" />
          </Field>
        ) : null}
        <Field label={t("minimum")} htmlFor="cp-min" hint={t("minimumHint")} error={err("minOrderValue")}>
          <Input id="cp-min" inputMode="decimal" value={values.minOrderValue} onChange={(e) => set("minOrderValue", e.target.value)} aria-invalid={!!errors.minOrderValue} autoComplete="off" />
        </Field>
      </div>
      {example !== null ? (
        <p className="text-sm text-muted" data-testid="coupon-example">
          {t("example", { basket: money(EXAMPLE_BASKET), discount: money(example), pay: money(EXAMPLE_BASKET - example) })}
        </p>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("startsAt")} htmlFor="cp-start" hint={t("dhakaHint")}>
          <Input id="cp-start" type="datetime-local" value={values.startsAt} onChange={(e) => set("startsAt", e.target.value)} />
        </Field>
        <Field label={t("endsAt")} htmlFor="cp-end" hint={t("endsHint")} error={err("endsAt")}>
          <Input id="cp-end" type="datetime-local" value={values.endsAt} min={values.startsAt || undefined} onChange={(e) => set("endsAt", e.target.value)} aria-invalid={!!errors.endsAt} />
        </Field>
        <Field label={t("limit")} htmlFor="cp-limit" hint={t("limitHint")} error={err("usageLimit")} className="sm:col-span-2">
          <Input id="cp-limit" inputMode="numeric" value={values.usageLimit} onChange={(e) => set("usageLimit", e.target.value)} aria-invalid={!!errors.usageLimit} autoComplete="off" />
        </Field>
      </div>
      {!isNew ? (
        <p className="text-sm text-muted" data-testid="coupon-used-note">
          {t("usedNote", { used: usedCount })}
        </p>
      ) : null}
      <p className="text-sm text-muted" data-testid="coupon-per-customer-note">
        {t("perCustomerNote")}
      </p>

      <div className="space-y-1">
        <ChoiceRow htmlFor="cp-active">
          <Switch id="cp-active" checked={values.isActive} onCheckedChange={(v) => set("isActive", v)} data-testid="coupon-active" />
          <span className="font-medium">{t("active")}</span>
        </ChoiceRow>
        <p className="text-sm text-muted ps-15">{values.isActive ? t("activeOn") : t("activeOff")}</p>
      </div>

      <div className="space-y-2">
        <ChoiceRow htmlFor="cp-stacks">
          <Switch id="cp-stacks" checked={values.stacksWithPromotions} onCheckedChange={(v) => set("stacksWithPromotions", v)} data-testid="coupon-stacks" />
          <span className="font-medium">{t("stacks")}</span>
        </ChoiceRow>
        <Alert variant="info" data-testid="coupon-stacks-explain">
          <span className="block space-y-1">
            <span className="block font-medium">{values.stacksWithPromotions ? t("stacksOnTitle") : t("stacksOffTitle")}</span>
            <span className="block">{values.stacksWithPromotions ? t("stacksOn") : t("stacksOff")}</span>
            <span className="block text-muted">{t("stacksBoth")}</span>
          </span>
        </Alert>
      </div>

      {failure ? (
        <Alert variant="danger" data-testid="coupon-form-error">
          {failure}
        </Alert>
      ) : null}
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="coupon-save">
          {isNew ? t("create") : t("save")}
        </Button>
      </div>
    </form>
  );
}
