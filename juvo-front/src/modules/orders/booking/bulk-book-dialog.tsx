"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleAlert, CircleCheck, Truck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { useBulkBook, useCouriers, type BulkBookResult } from "@/modules/couriers/api";
import { useCourierPermissions } from "@/modules/couriers/permissions";
import { useOrderErrorText } from "../api";
import { summarizeBulk } from "./logic";
import { useCodeText } from "./use-code-text";

type Props = { ids: string[]; open: boolean; onOpenChange: (open: boolean) => void; onDone: () => void };

const AUTO = "__auto";

function Body({ ids, close, onDone }: { ids: string[]; close: () => void; onDone: () => void }) {
  const t = useTranslations("booking.bulk");
  const tc = useTranslations("booking.common");
  const tl = useTranslations("orders.common");
  const errorText = useOrderErrorText();
  const codeText = useCodeText();
  const perms = useCourierPermissions();
  const couriers = useCouriers();
  const bulk = useBulkBook();
  const [courierId, setCourierId] = React.useState(AUTO);
  const [result, setResult] = React.useState<BulkBookResult | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);

  const options = React.useMemo(
    () => [
      { value: AUTO, label: t("auto") },
      ...(couriers.data ?? []).filter((c) => c.isEnabled && c.providerConfigured).map((c) => ({ value: c.id, label: c.name })),
    ],
    [couriers.data, t],
  );

  const run = async () => {
    setFailure(null);
    try {
      const answer = await bulk.mutateAsync({ orderIds: ids, courierId: courierId === AUTO ? null : courierId });
      setResult(answer);
      onDone();
    } catch (error) {
      setFailure(errorText(error));
    }
  };

  if (result) {
    const summary = summarizeBulk(result);
    return (
      <div className="space-y-4" data-testid="bulk-book-result">
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("resultTitle")}</DialogTitle>
        <DialogDescription className="text-muted" data-testid="bulk-book-summary">
          {t("summary", { ok: summary.ok, failed: summary.failed })}
        </DialogDescription>
        <ul className="max-h-80 space-y-2 overflow-y-auto" aria-label={t("resultTitle")}>
          {summary.rows.map((r) => (
            <li key={r.orderId} className="flex items-start gap-2 text-sm" data-ok={r.ok} data-testid="bulk-book-row">
              {r.ok ? <CircleCheck className="mt-0.5 size-5 shrink-0 text-success" aria-hidden /> : <CircleAlert className="mt-0.5 size-5 shrink-0 text-danger" aria-hidden />}
              <span className="min-w-0">
                <span className="font-mono font-semibold text-strong">{r.orderNumber ?? r.orderId}</span>{" "}
                <span className="text-muted [overflow-wrap:anywhere]">
                  {r.ok ? t("booked", { consignment: r.consignmentId ?? "" }) : codeText(r.errorCode, r.message ?? t("failedGeneric"))}
                </span>
              </span>
            </li>
          ))}
        </ul>
        <div className="flex justify-end">
          <Button onClick={close} data-testid="bulk-book-close">
            {tc("close")}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title", { count: ids.length })}</DialogTitle>
      <DialogDescription className="text-muted">{t("text")}</DialogDescription>
      {perms.courierView ? (
        <Field label={t("courier")} htmlFor="bulk-courier" hint={t("courierHint")}>
          <Select id="bulk-courier" value={courierId} onValueChange={setCourierId} options={options} placeholder={tl("choose")} searchPlaceholder={tl("search")} emptyText={tl("noMatch")} searchable={false} />
        </Field>
      ) : (
        <p className="text-sm text-muted">{t("autoOnly")}</p>
      )}
      {failure ? <Alert variant="danger">{failure}</Alert> : null}
      <div className="flex flex-wrap justify-end gap-2">
        <Button variant="secondary" onClick={close}>
          {tc("cancel")}
        </Button>
        <Button loading={bulk.isPending} onClick={() => void run()} data-testid="bulk-book-confirm">
          <Truck aria-hidden /> {t("confirm", { count: ids.length })}
        </Button>
      </div>
    </div>
  );
}

/** Books a courier for every selected packed order in one call (up to 50) and shows what happened to each. */
export default function BulkBookDialog({ ids, open, onOpenChange, onDone }: Props) {
  const tc = useTranslations("booking.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="md" closeLabel={tc("close")}>
        {open ? <Body ids={ids} close={() => onOpenChange(false)} onDone={onDone} /> : null}
      </ModalContent>
    </Dialog>
  );
}
