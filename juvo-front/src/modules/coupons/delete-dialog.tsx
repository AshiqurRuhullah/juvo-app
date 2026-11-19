"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { toNumber } from "@/modules/catalog/format";
import { useDeleteCoupon, useSaveCoupon, type Coupon } from "./api";
import { useCouponProblem } from "./common";
import { deletePlan, withActive, type DeletePlan } from "./logic";

/**
 * "Delete" asks first and says exactly what will happen. The API deletes a coupon that was never used and sits in no cart; for any other
 * coupon it refuses (COUPON.INACTIVE) so the history stays, and the coupon is switched off instead (a normal save with the active flag off).
 */
export function DeleteCouponDialog({ coupon, canSwitchOff, onClose }: { coupon: Coupon | null; canSwitchOff: boolean; onClose: () => void }) {
  const tc = useTranslations("coupons.common");
  return (
    <Dialog open={coupon !== null} onOpenChange={(open) => (open ? undefined : onClose())}>
      <ModalContent size="sm" closeLabel={tc("close")}>
        {coupon ? <Body coupon={coupon} canSwitchOff={canSwitchOff} close={onClose} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function Body({ coupon, canSwitchOff, close }: { coupon: Coupon; canSwitchOff: boolean; close: () => void }) {
  const t = useTranslations("coupons.remove");
  const tc = useTranslations("coupons.common");
  const format = useFormatter();
  const problem = useCouponProblem();
  const del = useDeleteCoupon();
  const save = useSaveCoupon();
  const used = toNumber(coupon.usedCount);
  // A coupon with no uses can still be refused when it sits in a customer's cart: then the same dialog turns into "switch off".
  const [inCart, setInCart] = React.useState(false);
  const [failure, setFailure] = React.useState<string | null>(null);
  const plan: DeletePlan = inCart ? (coupon.isActive ? (canSwitchOff ? "switchOff" : "noEditRight") : "alreadyOff") : deletePlan(coupon, { edit: canSwitchOff });
  const pending = del.isPending || save.isPending;

  const confirm = async () => {
    setFailure(null);
    try {
      if (plan === "delete") {
        await del.mutateAsync(coupon.id);
        toast.success(t("deleted", { code: coupon.code }));
      } else {
        await save.mutateAsync({ id: coupon.id, body: withActive(coupon, false) });
        toast.success(t("switchedOff", { code: coupon.code }));
      }
      close();
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (plan === "delete" && p?.code === "COUPON.INACTIVE") setInCart(true);
      else setFailure(problem(p));
    }
  };

  const canConfirm = plan === "delete" || plan === "switchOff";
  return (
    <>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{t(`${plan}.title`, { code: coupon.code })}</DialogTitle>
      <DialogDescription className="mt-2 text-muted" data-testid="remove-consequence" data-plan={plan}>
        {t(`${plan}.description`, { code: coupon.code, used: format.number(used) })}
      </DialogDescription>
      {inCart ? (
        <Alert variant="warning" className="mt-4" data-testid="remove-in-cart">
          {t("inCart")}
        </Alert>
      ) : null}
      {failure ? (
        <Alert variant="danger" className="mt-4" data-testid="remove-error">
          {failure}
        </Alert>
      ) : null}
      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button variant="secondary" disabled={pending}>
            {canConfirm ? tc("cancel") : tc("close")}
          </Button>
        </DialogClose>
        {canConfirm ? (
          <Button variant={plan === "delete" ? "danger" : "primary"} loading={pending} onClick={() => void confirm()} data-testid="remove-confirm">
            {t(`${plan}.confirm`)}
          </Button>
        ) : null}
      </div>
    </>
  );
}
