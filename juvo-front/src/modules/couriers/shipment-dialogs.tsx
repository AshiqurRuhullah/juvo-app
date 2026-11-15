"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, Label, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useAdvanceShipment, useCancelShipment, useRebookShipment, useSuggestions, type Shipment } from "./api";
import { ADVANCE_RAW, defaultNext, parseOptionalInt, rawForStatus } from "./admin-logic";

export type DialogKind = "cancel" | "rebook" | "advance";
type Props = { kind: DialogKind | null; shipment: Shipment; onClose: () => void };

/** The three action dialogs of a parcel. One lazy chunk, mounted only while a dialog is open so every field starts fresh. */
export default function ShipmentDialogs({ kind, shipment, onClose }: Props) {
  const tc = useTranslations("shipments.common");
  return (
    <Dialog open={kind !== null} onOpenChange={(open) => (open ? undefined : onClose())}>
      <ModalContent size="sm" closeLabel={tc("close")} className={kind === "rebook" ? "md:max-w-xl" : undefined}>
        {kind === "cancel" ? <CancelBody shipment={shipment} onClose={onClose} /> : null}
        {kind === "rebook" ? <RebookBody shipment={shipment} onClose={onClose} /> : null}
        {kind === "advance" ? <AdvanceBody shipment={shipment} onClose={onClose} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function Footer({ busy, confirm, destructive, testId }: { busy: boolean; confirm: string; destructive?: boolean; testId: string }) {
  const tc = useTranslations("shipments.common");
  return (
    <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
      <DialogClose asChild>
        <Button type="button" variant="secondary" disabled={busy}>
          {tc("cancel")}
        </Button>
      </DialogClose>
      <Button type="submit" variant={destructive ? "danger" : "primary"} loading={busy} data-testid={testId}>
        {confirm}
      </Button>
    </div>
  );
}

function CancelBody({ shipment, onClose }: { shipment: Shipment; onClose: () => void }) {
  const t = useTranslations("shipments.dialogs.cancel");
  const problemMessage = useProblemMessage();
  const cancel = useCancelShipment();
  const [reason, setReason] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    if (!reason.trim()) {
      setError(t("reasonRequired"));
      return;
    }
    setError(null);
    try {
      await cancel.mutateAsync({ id: shipment.id, reason: reason.trim() });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{t("description", { consignment: shipment.consignmentId })}</DialogDescription>
      <div className="mt-4 space-y-4">
        <Field label={t("reason")} htmlFor="cancel-reason" required hint={t("reasonHint")} error={error ?? undefined}>
          <Textarea id="cancel-reason" rows={3} maxLength={300} value={reason} onChange={(e) => setReason(e.target.value)} aria-invalid={!!error} />
        </Field>
        {failure ? <Alert variant="danger" data-testid="dialog-error">{failure}</Alert> : null}
      </div>
      <Footer busy={cancel.isPending} confirm={t("confirm")} destructive testId="cancel-confirm" />
    </form>
  );
}

function RebookBody({ shipment, onClose }: { shipment: Shipment; onClose: () => void }) {
  const t = useTranslations("shipments.dialogs.rebook");
  const money = useMoney();
  const router = useRouter();
  const problemMessage = useProblemMessage();
  const suggestions = useSuggestions(shipment.orderId, undefined, true);
  const rebook = useRebookShipment();
  const [courier, setCourier] = React.useState("auto");
  const [weight, setWeight] = React.useState(String(toNumber(shipment.weightGrams)));
  const [length, setLength] = React.useState(shipment.lengthCm === null ? "" : String(toNumber(shipment.lengthCm)));
  const [width, setWidth] = React.useState(shipment.widthCm === null ? "" : String(toNumber(shipment.widthCm)));
  const [height, setHeight] = React.useState(shipment.heightCm === null ? "" : String(toNumber(shipment.heightCm)));
  const [note, setNote] = React.useState(shipment.note ?? "");
  const [reason, setReason] = React.useState("");
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [failure, setFailure] = React.useState<string | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const w = parseOptionalInt(weight, 1, 100_000);
    const l = parseOptionalInt(length, 1, 300);
    const wd = parseOptionalInt(width, 1, 300);
    const h = parseOptionalInt(height, 1, 300);
    const next: Record<string, string> = {};
    if (w === undefined) next.weight = t("weightInvalid");
    if (l === undefined) next.length = t("sizeInvalid");
    if (wd === undefined) next.width = t("sizeInvalid");
    if (h === undefined) next.height = t("sizeInvalid");
    setErrors(next);
    if (Object.keys(next).length) return;
    try {
      const result = await rebook.mutateAsync({
        id: shipment.id,
        body: { courierId: courier === "auto" ? null : courier, weightGrams: w ?? null, lengthCm: l ?? null, widthCm: wd ?? null, heightCm: h ?? null, note: note.trim() || null, reason: reason.trim() || null },
      });
      toast.success(t("done", { consignment: result.consignmentId }));
      onClose();
      if (result.id !== shipment.id) router.push(`/admin/shipments/${result.id}`);
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  const options = suggestions.data ?? [];
  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{shipment.status === "Booked" ? t("descriptionBooked") : t("descriptionCancelled")}</DialogDescription>
      <div className="mt-4 space-y-4">
        <div className="space-y-1.5">
          <Label id="rb-courier-label">{t("courier")}</Label>
          {suggestions.isPending ? (
            <Skeleton className="h-24" />
          ) : (
            <RadioGroup value={courier} onValueChange={setCourier} aria-labelledby="rb-courier-label" className="grid gap-2">
              <ChoiceBox id="rb-auto" value="auto" title={t("auto")} hint={t("autoHint")} />
              {options.map((o) => (
                <ChoiceBox
                  key={o.courierId}
                  id={`rb-${o.code}`}
                  value={o.courierId}
                  disabled={!o.eligible}
                  title={o.name}
                  hint={o.eligible ? [o.recommended ? t("recommended") : null, o.cost !== null ? money(toNumber(o.cost)) : null, o.daysMin !== null && o.daysMax !== null ? t("days", { min: toNumber(o.daysMin), max: toNumber(o.daysMax) }) : null].filter(Boolean).join(" · ") : (o.reason ?? t("notEligible"))}
                />
              ))}
            </RadioGroup>
          )}
          {suggestions.isError ? <p className="text-sm text-muted">{t("suggestionsFailed")}</p> : null}
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("weight")} htmlFor="rb-weight" error={errors.weight}>
            <Input id="rb-weight" inputMode="numeric" value={weight} onChange={(e) => setWeight(e.target.value)} aria-invalid={!!errors.weight} autoComplete="off" />
          </Field>
          <div className="grid grid-cols-3 gap-2">
            <Field label={t("length")} htmlFor="rb-l" error={errors.length}>
              <Input id="rb-l" inputMode="numeric" value={length} onChange={(e) => setLength(e.target.value)} aria-invalid={!!errors.length} autoComplete="off" />
            </Field>
            <Field label={t("width")} htmlFor="rb-w" error={errors.width}>
              <Input id="rb-w" inputMode="numeric" value={width} onChange={(e) => setWidth(e.target.value)} aria-invalid={!!errors.width} autoComplete="off" />
            </Field>
            <Field label={t("height")} htmlFor="rb-h" error={errors.height}>
              <Input id="rb-h" inputMode="numeric" value={height} onChange={(e) => setHeight(e.target.value)} aria-invalid={!!errors.height} autoComplete="off" />
            </Field>
          </div>
        </div>
        <Field label={t("note")} htmlFor="rb-note">
          <Textarea id="rb-note" rows={2} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        <Field label={t("reason")} htmlFor="rb-reason" hint={t("reasonHint")}>
          <Textarea id="rb-reason" rows={2} maxLength={300} value={reason} onChange={(e) => setReason(e.target.value)} />
        </Field>
        {failure ? <Alert variant="danger" data-testid="dialog-error">{failure}</Alert> : null}
      </div>
      <Footer busy={rebook.isPending} confirm={t("confirm")} testId="rebook-confirm" />
    </form>
  );
}

function ChoiceBox({ id, value, title, hint, disabled }: { id: string; value: string; title: string; hint?: string; disabled?: boolean }) {
  return (
    <div className="flex min-h-12 items-center gap-3 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
      <RadioItem id={id} value={value} disabled={disabled} />
      <Label htmlFor={id} className={disabled ? "flex min-h-11 flex-1 cursor-not-allowed flex-col justify-center opacity-70" : "flex min-h-11 flex-1 cursor-pointer flex-col justify-center"}>
        <span>{title}</span>
        {hint ? <span className="text-xs font-normal text-muted">{hint}</span> : null}
      </Label>
    </div>
  );
}

/** The sandbox simulator only: moves the parcel as a real courier's report would. Labelled as such on every line. */
function AdvanceBody({ shipment, onClose }: { shipment: Shipment; onClose: () => void }) {
  const t = useTranslations("shipments.dialogs.advance");
  const ts = useTranslations("shipments.advanceStatus");
  const problemMessage = useProblemMessage();
  const advance = useAdvanceShipment();
  const next = defaultNext(shipment.nextStatuses);
  const choices = React.useMemo(() => {
    const raws = shipment.nextStatuses.map(rawForStatus).filter((r): r is string => r !== null);
    // A scan at a hub changes no status, so it is always available while the parcel is moving.
    return ADVANCE_RAW.filter((r) => raws.includes(r) || r === "at_hub");
  }, [shipment.nextStatuses]);
  const [status, setStatus] = React.useState<string>(next ? (rawForStatus(next) ?? "") : "");
  const [note, setNote] = React.useState("");
  const [failure, setFailure] = React.useState<string | null>(null);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    try {
      await advance.mutateAsync({ id: shipment.id, status: status || null, note: note.trim() || null });
      toast.success(t("done"));
      onClose();
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{t("description")}</DialogDescription>
      <div className="mt-4 space-y-4">
        <div className="space-y-1.5">
          <Label id="adv-label">{t("status")}</Label>
          <RadioGroup value={status} onValueChange={setStatus} aria-labelledby="adv-label" className="grid gap-2">
            {choices.map((raw) => (
              <ChoiceBox key={raw} id={`adv-${raw}`} value={raw} title={ts(raw)} />
            ))}
          </RadioGroup>
        </div>
        <Field label={t("note")} htmlFor="adv-note" hint={t("noteHint")}>
          <Textarea id="adv-note" rows={2} maxLength={300} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        {failure ? <Alert variant="danger" data-testid="dialog-error">{failure}</Alert> : null}
      </div>
      <Footer busy={advance.isPending} confirm={t("confirm")} testId="advance-confirm" />
    </form>
  );
}
