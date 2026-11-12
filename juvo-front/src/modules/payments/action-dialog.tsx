"use client";

import * as React from "react";
import { Button } from "@/components/ui/button";
import { Field, Input, Label, RadioGroup, RadioItem, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";

export type DialogField = {
  name: string;
  label: string;
  kind?: "text" | "textarea" | "number" | "radio";
  required?: boolean;
  hint?: string;
  initial?: string;
  options?: { value: string; label: string; hint?: string }[];
  max?: number;
};

type Props = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description: string;
  confirmLabel: string;
  cancelLabel: string;
  closeLabel: string;
  requiredText: string;
  destructive?: boolean;
  fields: DialogField[];
  /** Returns null when it worked (the dialog closes) or the text to show when it did not. */
  onSubmit: (values: Record<string, string>) => Promise<string | null>;
};

/** The body is mounted only while the dialog is open, so its fields start fresh every time (no reset effect needed). */
function Body({ title, description, confirmLabel, cancelLabel, requiredText, destructive, fields, onSubmit, close, idBase }: Omit<Props, "open" | "onOpenChange" | "closeLabel"> & { close: () => void; idBase: string }) {
  const [values, setValues] = React.useState<Record<string, string>>(() => Object.fromEntries(fields.map((f) => [f.name, f.initial ?? ""])));
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    for (const f of fields) if (f.required && !values[f.name]?.trim()) next[f.name] = requiredText;
    setErrors(next);
    setFailure(null);
    if (Object.keys(next).length) return;
    setBusy(true);
    const result = await onSubmit(Object.fromEntries(Object.entries(values).map(([k, v]) => [k, v.trim()])));
    setBusy(false);
    if (result === null) close();
    else setFailure(result);
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{title}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{description}</DialogDescription>
      <div className="mt-4 space-y-4">
        {fields.map((f) => {
          const id = `${idBase}-${f.name}`;
          const set = (v: string) => setValues((old) => ({ ...old, [f.name]: v }));
          if (f.kind === "radio") {
            return (
              <div key={f.name} className="space-y-1.5">
                <Label id={`${id}-label`}>{f.label}</Label>
                <RadioGroup value={values[f.name]} onValueChange={set} aria-labelledby={`${id}-label`} className="grid gap-2">
                  {(f.options ?? []).map((o) => (
                    <div key={o.value} className="flex min-h-12 items-center gap-3 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                      <RadioItem id={`${id}-${o.value}`} value={o.value} />
                      <Label htmlFor={`${id}-${o.value}`} className="flex min-h-11 flex-1 cursor-pointer flex-col justify-center">
                        <span>{o.label}</span>
                        {o.hint ? <span className="text-xs font-normal text-muted">{o.hint}</span> : null}
                      </Label>
                    </div>
                  ))}
                </RadioGroup>
              </div>
            );
          }
          return (
            <Field key={f.name} label={f.label} htmlFor={id} required={f.required} hint={f.hint} error={errors[f.name]}>
              {f.kind === "textarea" ? (
                <Textarea id={id} rows={3} maxLength={f.max ?? 500} value={values[f.name]} onChange={(e) => set(e.target.value)} aria-invalid={!!errors[f.name]} />
              ) : (
                <Input id={id} type={f.kind === "number" ? "number" : "text"} inputMode={f.kind === "number" ? "decimal" : undefined} step={f.kind === "number" ? "0.01" : undefined} min={f.kind === "number" ? "0" : undefined} maxLength={f.max} value={values[f.name]} onChange={(e) => set(e.target.value)} aria-invalid={!!errors[f.name]} autoComplete="off" />
              )}
            </Field>
          );
        })}
        {failure ? <Alert variant="danger" data-testid="dialog-error">{failure}</Alert> : null}
      </div>
      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={busy}>
            {cancelLabel}
          </Button>
        </DialogClose>
        <Button type="submit" variant={destructive ? "danger" : "primary"} loading={busy} data-testid="dialog-confirm">
          {confirmLabel}
        </Button>
      </div>
    </form>
  );
}

/** A small form in a dialog for a staff action that needs a reason, a reference or an amount (approve, reject, refund, settle ...). */
export function ActionDialog({ open, onOpenChange, closeLabel, ...rest }: Props) {
  const idBase = React.useId();
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="sm" closeLabel={closeLabel}>
        <Body {...rest} close={() => onOpenChange(false)} idBase={idBase} />
      </ModalContent>
    </Dialog>
  );
}
