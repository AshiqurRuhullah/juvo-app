"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { CircleAlert, CircleCheck, Printer, Truck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, Label, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDebounced } from "@/lib/hooks";
import { useBookShipment, useSuggestions, type Shipment } from "@/modules/couriers/api";
import { useCourierPermissions } from "@/modules/couriers/permissions";
import { formatMoney, safeTrackingUrl } from "@/modules/couriers/status";
import { useOrderErrorText } from "../api";
import { buildBookRequest, defaultCourier, orderSuggestions, parseCm, parseWeightKg } from "./logic";

type Props = { orderId: string; orderNumber: string; branchId: string; open: boolean; onOpenChange: (open: boolean) => void };

/** Mounted only while open, so every opening starts with empty fields and fresh suggestions. */
function Body({ orderId, orderNumber, branchId, close }: Omit<Props, "open" | "onOpenChange"> & { close: () => void }) {
  const t = useTranslations("booking.book");
  const tc = useTranslations("booking.common");
  const errorText = useOrderErrorText();
  const perms = useCourierPermissions(branchId);
  const book = useBookShipment();
  const [weight, setWeight] = React.useState("");
  const [length, setLength] = React.useState("");
  const [width, setWidth] = React.useState("");
  const [height, setHeight] = React.useState("");
  const [note, setNote] = React.useState("");
  const [choice, setChoice] = React.useState("");
  const [failure, setFailure] = React.useState<string | null>(null);
  const [booked, setBooked] = React.useState<Shipment | null>(null);

  const weightGrams = parseWeightKg(weight);
  const lengthCm = parseCm(length);
  const widthCm = parseCm(width);
  const heightCm = parseCm(height);
  // A short pause after typing, so the suggestions are not asked for on every keystroke.
  const debouncedWeight = useDebounced(typeof weightGrams === "number" ? weightGrams : null, 400);
  const suggestions = useSuggestions(orderId, debouncedWeight);
  const grouped = React.useMemo(() => orderSuggestions(suggestions.data ?? []), [suggestions.data]);
  const selected = defaultCourier(suggestions.data ?? [], choice);
  const invalid = weightGrams === null || lengthCm === null || widthCm === null || heightCm === null;
  const recommended = grouped.eligible.find((s) => s.recommended)?.courierId;

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (invalid || !selected) return;
    setFailure(null);
    try {
      const shipment = await book.mutateAsync({
        orderId,
        body: buildBookRequest({ courierId: selected, weightGrams: weightGrams ?? undefined, lengthCm: lengthCm ?? undefined, widthCm: widthCm ?? undefined, heightCm: heightCm ?? undefined, note }),
      });
      setBooked(shipment);
      toast.success(t("done", { consignment: shipment.consignmentId }));
    } catch (error) {
      setFailure(errorText(error));
    }
  };

  if (booked) {
    const link = safeTrackingUrl(booked.trackingUrl);
    return (
      <div className="space-y-4" data-testid="booked-result">
        <DialogTitle className="flex items-center gap-2 font-display text-xl font-semibold text-strong">
          <CircleCheck className="size-6 text-success" aria-hidden /> {t("bookedTitle")}
        </DialogTitle>
        <DialogDescription className="text-muted">{t("bookedText", { number: orderNumber, courier: booked.courierName })}</DialogDescription>
        <dl className="space-y-1.5 text-sm">
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t("consignment")}</dt>
            <dd className="font-mono font-semibold text-strong" data-testid="booked-consignment">
              {booked.consignmentId}
            </dd>
          </div>
          <div className="flex justify-between gap-3">
            <dt className="text-muted">{t("cost")}</dt>
            <dd className="text-strong">{formatMoney(Number(booked.cost ?? 0))}</dd>
          </div>
          {Number(booked.codAmount) > 0 ? (
            <div className="flex justify-between gap-3">
              <dt className="text-muted">{t("cod")}</dt>
              <dd className="text-strong">{formatMoney(Number(booked.codAmount))}</dd>
            </div>
          ) : null}
        </dl>
        {link ? (
          <a href={link} target="_blank" rel="noopener noreferrer" className="inline-flex min-h-11 items-center font-semibold text-strong underline underline-offset-4">
            {tc("trackingLink")}
          </a>
        ) : null}
        <div className="flex flex-wrap justify-end gap-2 pt-2">
          {perms.printLabel ? (
            <Button asChild variant="secondary">
              <Link href={`/admin/shipments/${booked.id}/label`} data-testid="booked-label">
                <Printer aria-hidden /> {t("printLabel")}
              </Link>
            </Button>
          ) : null}
          <Button onClick={close} data-testid="booked-close">
            {tc("close")}
          </Button>
        </div>
      </div>
    );
  }

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="space-y-5" aria-label={t("title", { number: orderNumber })}>
      <div>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title", { number: orderNumber })}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{t("text")}</DialogDescription>
      </div>

      <div className="space-y-2" data-testid="suggestions">
        <h3 className="text-sm font-semibold text-strong">{t("couriers")}</h3>
        {suggestions.isLoading ? <Skeleton className="h-28" aria-busy /> : null}
        {suggestions.isError ? <Alert variant="danger">{errorText(suggestions.error)}</Alert> : null}
        {suggestions.data && grouped.eligible.length === 0 ? (
          <Alert variant="warning" data-testid="no-courier">
            {t("noneEligible")}
          </Alert>
        ) : null}
        {grouped.eligible.length > 0 ? (
          <RadioGroup value={selected} onValueChange={setChoice} className="grid gap-2" aria-label={t("couriers")}>
            {grouped.eligible.map((s) => (
              <div key={s.courierId} className="flex min-h-12 items-start gap-3 rounded-control border border-line bg-card p-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft" data-testid="courier-option" data-code={s.code}>
                <RadioItem id={`bk-${s.courierId}`} value={s.courierId} className="mt-0.5" />
                <Label htmlFor={`bk-${s.courierId}`} className="min-h-11 flex-1 cursor-pointer font-normal">
                  <span className="flex flex-wrap items-center gap-2 font-semibold text-strong">
                    {s.name}
                    {s.courierId === recommended ? <span className="rounded-full bg-success-soft px-2 py-0.5 text-xs font-medium text-success-ink">{t("recommended")}</span> : null}
                  </span>
                  <span className="block text-sm text-muted">
                    {t("cost")}: {s.cost === null ? "-" : formatMoney(Number(s.cost))}
                    {s.daysMin !== null && s.daysMax !== null ? ` · ${t("days", { min: Number(s.daysMin), max: Number(s.daysMax) })}` : ""}
                  </span>
                </Label>
              </div>
            ))}
          </RadioGroup>
        ) : null}
        {grouped.ineligible.length > 0 ? (
          <ul className="space-y-1.5 border-t border-line pt-3" aria-label={t("unavailable")} data-testid="ineligible">
            {grouped.ineligible.map((s) => (
              <li key={s.courierId} className="flex items-start gap-2 text-sm text-muted" data-code={s.code}>
                <CircleAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
                <span>
                  <span className="font-medium">{s.name}</span>: <span data-testid="ineligible-reason">{s.reason ?? t("unavailableNoReason")}</span>
                </span>
              </li>
            ))}
          </ul>
        ) : null}
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("weight")} htmlFor="bk-weight" hint={t("weightHint")} error={weightGrams === null ? t("weightInvalid") : undefined}>
          <Input id="bk-weight" inputMode="decimal" value={weight} onChange={(e) => setWeight(e.target.value)} aria-invalid={weightGrams === null} autoComplete="off" />
        </Field>
        <fieldset className="min-w-0">
          <legend className="mb-1.5 text-sm font-medium text-strong">{t("dimensions")}</legend>
          <div className="grid grid-cols-3 gap-2">
            {[
              ["bk-length", t("length"), length, setLength, lengthCm],
              ["bk-width", t("width"), width, setWidth, widthCm],
              ["bk-height", t("height"), height, setHeight, heightCm],
            ].map(([id, label, value, set, parsed]) => (
              <div key={id as string}>
                <label htmlFor={id as string} className="sr-only">
                  {label as string}
                </label>
                <Input id={id as string} inputMode="numeric" placeholder={label as string} value={value as string} onChange={(e) => (set as (v: string) => void)(e.target.value)} aria-invalid={parsed === null} autoComplete="off" />
              </div>
            ))}
          </div>
          {lengthCm === null || widthCm === null || heightCm === null ? (
            <p role="alert" className="mt-1 text-sm font-medium text-danger">
              {t("dimensionsInvalid")}
            </p>
          ) : (
            <p className="mt-1 text-sm text-muted">{t("dimensionsHint")}</p>
          )}
        </fieldset>
      </div>
      <Field label={t("note")} htmlFor="bk-note" hint={tc("optional")}>
        <Textarea id="bk-note" rows={2} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
      </Field>

      {failure ? (
        <Alert variant="danger" data-testid="book-error">
          {failure}
        </Alert>
      ) : null}

      <div className="flex flex-wrap justify-end gap-2">
        <Button type="button" variant="secondary" onClick={close}>
          {tc("cancel")}
        </Button>
        <Button type="submit" loading={book.isPending} disabled={invalid || !selected} data-testid="book-confirm">
          <Truck aria-hidden /> {t("confirm")}
        </Button>
      </div>
    </form>
  );
}

/** The "Book courier" dialog of one packed order: suggestions with the recommended courier preselected, then the booking. */
export default function BookCourierDialog({ open, onOpenChange, ...rest }: Props) {
  const tc = useTranslations("booking.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        {open ? <Body {...rest} close={() => onOpenChange(false)} /> : null}
      </ModalContent>
    </Dialog>
  );
}
