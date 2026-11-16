"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Checkbox, ChoiceRow, Field, Input, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { toast } from "@/components/ui/toaster";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useSaveSupplier, type Supplier } from "./api";
import { parseQuantity } from "./logic";

/** Create or change a supplier. The body is mounted only while the dialog is open, so the fields start fresh every time. */
export function SupplierDialog({ open, onOpenChange, supplier }: { open: boolean; onOpenChange: (open: boolean) => void; supplier: Supplier | null }) {
  const t = useTranslations("purchasing.suppliers.form");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="md" closeLabel={t("close")}>
        <Body supplier={supplier} close={() => onOpenChange(false)} />
      </ModalContent>
    </Dialog>
  );
}

function Body({ supplier, close }: { supplier: Supplier | null; close: () => void }) {
  const t = useTranslations("purchasing.suppliers.form");
  const tc = useTranslations("purchasing.common");
  const report = useReportError();
  const save = useSaveSupplier();
  const [v, setV] = React.useState({
    name: supplier?.name ?? "",
    contactName: supplier?.contactName ?? "",
    phone: supplier?.phone ?? "",
    email: supplier?.email ?? "",
    address: supplier?.address ?? "",
    terms: String(supplier ? toNumber(supplier.paymentTermsDays) : 0),
    taxId: supplier?.taxId ?? "",
    notes: supplier?.notes ?? "",
    active: supplier?.isActive ?? true,
  });
  const [errors, setErrors] = React.useState<{ name?: string; terms?: string }>({});
  const set = (key: keyof typeof v) => (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setV((old) => ({ ...old, [key]: e.target.value }));

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: typeof errors = {};
    if (!v.name.trim()) next.name = t("nameRequired");
    const terms = parseQuantity(v.terms);
    if (terms === null || terms > 365) next.terms = t("termsInvalid");
    setErrors(next);
    if (Object.keys(next).length) return;
    const text = (s: string) => s.trim() || null;
    try {
      await save.mutateAsync({
        id: supplier?.id,
        body: { name: v.name.trim(), contactName: text(v.contactName), phone: text(v.phone), email: text(v.email), address: text(v.address), paymentTermsDays: terms, taxId: text(v.taxId), notes: text(v.notes), isActive: v.active },
      });
      toast.success(supplier ? t("saved") : t("created"));
      close();
    } catch (e) {
      report(e);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{supplier ? t("editTitle") : t("newTitle")}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{t("description")}</DialogDescription>
      <div className="mt-4 grid gap-4 sm:grid-cols-2">
        <Field label={t("name")} htmlFor="sp-name" required error={errors.name} className="sm:col-span-2">
          <Input id="sp-name" value={v.name} maxLength={160} onChange={set("name")} aria-invalid={!!errors.name} autoComplete="off" />
        </Field>
        <Field label={t("contact")} htmlFor="sp-contact">
          <Input id="sp-contact" value={v.contactName} maxLength={120} onChange={set("contactName")} autoComplete="off" />
        </Field>
        <Field label={t("phone")} htmlFor="sp-phone">
          <Input id="sp-phone" type="tel" inputMode="tel" value={v.phone} maxLength={40} onChange={set("phone")} autoComplete="off" />
        </Field>
        <Field label={t("email")} htmlFor="sp-email">
          <Input id="sp-email" type="email" value={v.email} maxLength={200} onChange={set("email")} autoComplete="off" />
        </Field>
        <Field label={t("terms")} htmlFor="sp-terms" hint={t("termsHint")} error={errors.terms}>
          <Input id="sp-terms" inputMode="numeric" value={v.terms} onChange={set("terms")} aria-invalid={!!errors.terms} autoComplete="off" />
        </Field>
        <Field label={t("taxId")} htmlFor="sp-tax">
          <Input id="sp-tax" value={v.taxId} maxLength={60} onChange={set("taxId")} autoComplete="off" />
        </Field>
        <Field label={t("address")} htmlFor="sp-address" className="sm:col-span-2">
          <Textarea id="sp-address" rows={2} maxLength={400} value={v.address} onChange={set("address")} />
        </Field>
        <Field label={t("notes")} htmlFor="sp-notes" className="sm:col-span-2">
          <Textarea id="sp-notes" rows={2} maxLength={1000} value={v.notes} onChange={set("notes")} />
        </Field>
        {supplier ? (
          <ChoiceRow htmlFor="sp-active" className="sm:col-span-2">
            <Checkbox id="sp-active" checked={v.active} onCheckedChange={(c) => setV((old) => ({ ...old, active: c === true }))} />
            {t("active")}
          </ChoiceRow>
        ) : null}
      </div>
      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="supplier-save">
          {tc("save")}
        </Button>
      </div>
    </form>
  );
}
