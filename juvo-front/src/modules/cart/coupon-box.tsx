"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { TicketPercent, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input, Label } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useApplyCoupon, useRemoveCoupon, type CartView } from "@/modules/store/api";
import { useMoney } from "@/modules/catalog/format";
import { useCartErrorText } from "./use-cart-error";

/** Coupon code box: apply, see the discount, remove. COUPON.* answers are explained in plain words. */
export function CouponBox({ coupon }: { coupon: CartView["coupon"] }) {
  const t = useTranslations("cart.coupon");
  const money = useMoney();
  const errorText = useCartErrorText();
  const apply = useApplyCoupon();
  const remove = useRemoveCoupon();
  const [code, setCode] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const id = React.useId();

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setError(null);
    const value = code.trim();
    if (!value) {
      setError(t("enter"));
      return;
    }
    try {
      await apply.mutateAsync(value);
      setCode("");
      toast.success(t("applied", { code: value.toUpperCase() }));
    } catch (failure) {
      setError(errorText(failure));
    }
  };

  if (coupon) {
    return (
      <div className="space-y-2" data-testid="coupon-applied">
        <div className="flex items-start justify-between gap-3 rounded-control bg-success-soft p-3">
          <div className="min-w-0">
            <p className="flex flex-wrap items-center gap-2 font-semibold text-success-ink">
              <TicketPercent className="size-5 shrink-0" aria-hidden /> {t("appliedTitle")} <Badge variant="success">{coupon.code}</Badge>
            </p>
            {coupon.description ? <p className="mt-0.5 text-sm text-success-ink/90 [overflow-wrap:anywhere]">{coupon.description}</p> : null}
            <p className="mt-0.5 text-sm font-medium text-success-ink">{t("saves", { amount: money(coupon.discount) })}</p>
          </div>
          <Button variant="ghost" size="icon" className="shrink-0" aria-label={t("remove", { code: coupon.code })} loading={remove.isPending} onClick={() => remove.mutate()}>
            <X aria-hidden />
          </Button>
        </div>
        {coupon.problem ? <Alert variant="warning">{t("noLongerApplies")}</Alert> : null}
      </div>
    );
  }

  return (
    <form onSubmit={submit} noValidate className="space-y-2">
      <Label htmlFor={id}>{t("title")}</Label>
      <div className="flex gap-2">
        <Input id={id} value={code} onChange={(e) => setCode(e.target.value)} placeholder={t("placeholder")} autoComplete="off" autoCapitalize="characters" aria-invalid={!!error} aria-describedby={error ? `${id}-error` : undefined} className="uppercase placeholder:normal-case" />
        <Button type="submit" variant="secondary" loading={apply.isPending}>
          {t("apply")}
        </Button>
      </div>
      {error ? (
        <p id={`${id}-error`} role="alert" className="text-sm font-medium text-danger">
          {error}
        </p>
      ) : null}
    </form>
  );
}
