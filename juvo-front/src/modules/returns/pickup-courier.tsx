"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ChoiceRow, Field, Switch } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { useCouriers } from "@/modules/couriers/api";
import { useCourierPermissions } from "@/modules/couriers/permissions";

export type PickupCourier = { use: boolean; courierId: string };
export const noPickupCourier: PickupCourier = { use: false, courierId: "" };

const AUTO = "__auto";

/** The courier list is asked for only when the switch is on (and the person may see couriers). */
function CourierChoice({ value, onChange }: { value: PickupCourier; onChange: (next: PickupCourier) => void }) {
  const t = useTranslations("booking.returns");
  const tc = useTranslations("returns.common");
  const couriers = useCouriers();
  const options = React.useMemo(
    () => [{ value: AUTO, label: t("auto") }, ...(couriers.data ?? []).filter((c) => c.isEnabled && c.providerConfigured).map((c) => ({ value: c.id, label: c.name }))],
    [couriers.data, t],
  );
  return (
    <Field label={t("courier")} htmlFor="pickup-courier-choice">
      <Select
        id="pickup-courier-choice"
        value={value.courierId || AUTO}
        onValueChange={(v) => onChange({ ...value, courierId: v === AUTO ? "" : v })}
        options={options}
        placeholder={tc("choose")}
        searchPlaceholder={tc("search")}
        emptyText={tc("noMatch")}
        searchable={false}
      />
    </Field>
  );
}

/**
 * "Book a courier for the pickup" in the schedule-pickup dialog. When on, the system books a reverse parcel (the courier may be chosen,
 * else the best eligible one); when off the manual courier-name field next to it works exactly as before. Offered only to people who
 * may book parcels in the return's branch.
 */
export function PickupCourierFields({ branchId, value, onChange }: { branchId: string; value: PickupCourier; onChange: (next: PickupCourier) => void }) {
  const t = useTranslations("booking.returns");
  const perms = useCourierPermissions(branchId);
  if (!perms.book) return null;

  return (
    <div className="space-y-3 rounded-control border border-line p-3" data-testid="pickup-courier">
      <ChoiceRow htmlFor="pickup-use-courier">
        <Switch id="pickup-use-courier" checked={value.use} onCheckedChange={(v) => onChange({ ...value, use: v })} />
        <span>
          <span className="block">{t("use")}</span>
          <span className="block text-sm font-normal text-muted">{t("useHint")}</span>
        </span>
      </ChoiceRow>
      {value.use && perms.courierView ? <CourierChoice value={value} onChange={onChange} /> : null}
    </div>
  );
}
