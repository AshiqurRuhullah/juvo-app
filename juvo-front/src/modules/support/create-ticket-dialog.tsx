"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import type { UploadedFile } from "@/components/data/attachment-picker";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { fieldErrors } from "@/lib/api/problem";
import { useCreateTicket } from "./api";
import { TicketAttachments, useAssigneeOptions, useCategoryOptions, useSupportPermissions } from "./common";
import { MESSAGE_MAX, STAFF_CHANNELS, SUBJECT_MAX, TICKET_PRIORITIES, hasErrors, validateStaffTicket, type StaffTicketForm, type TicketChannel, type TicketPriority } from "./logic";

const EMPTY: StaffTicketForm = { mobile: "", customerName: "", email: "", subject: "", message: "", channel: "Phone", priority: "Normal", category: "other", orderNumber: "" };

/** A ticket opened by staff for a customer (a phone call, a WhatsApp or Facebook message): FR-Y01. Default export so it can be loaded on demand. */
export default function CreateTicketDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="lg" closeLabel={useTranslations("support.common")("close")}>
        <Body onClose={() => onOpenChange(false)} />
      </ModalContent>
    </Dialog>
  );
}

function Body({ onClose }: { onClose: () => void }) {
  const t = useTranslations("support.create");
  const tc = useTranslations("support.common");
  const tch = useTranslations("support.channel");
  const tp = useTranslations("support.priority");
  const router = useRouter();
  const problemMessage = useProblemMessage();
  const perms = useSupportPermissions();
  const categories = useCategoryOptions();
  const assignees = useAssigneeOptions(perms.assign);
  const create = useCreateTicket();
  const [form, setForm] = React.useState<StaffTicketForm>(EMPTY);
  const [assignee, setAssignee] = React.useState("");
  const [files, setFiles] = React.useState<UploadedFile[]>([]);
  const [errors, setErrors] = React.useState<ReturnType<typeof validateStaffTicket>>({});
  const [serverErrors, setServerErrors] = React.useState<Record<string, string>>({});
  const [problem, setProblem] = React.useState<string | null>(null);
  const sending = React.useRef(false);

  const set = <K extends keyof StaffTicketForm>(key: K, value: StaffTicketForm[K]) => setForm((f) => ({ ...f, [key]: value }));
  const errorText = (key: keyof typeof errors) => {
    const e = errors[key];
    return e ? t(`errors.${e}`, { max: key === "message" ? MESSAGE_MAX : SUBJECT_MAX }) : serverErrors[key];
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (sending.current) return;
    const found = validateStaffTicket(form);
    setErrors(found);
    setServerErrors({});
    setProblem(null);
    if (hasErrors(found)) return;
    sending.current = true;
    try {
      const detail = await create.mutateAsync({
        ...form,
        channel: form.channel as TicketChannel,
        priority: form.priority as TicketPriority,
        assigneeId: perms.assign && assignee ? assignee : null,
        attachmentPaths: files.map((f) => f.path!).filter(Boolean),
      });
      toast.success(t("created", { number: detail.number }));
      onClose();
      router.push(`/admin/support/${detail.id}`);
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.errors) setServerErrors(fieldErrors(p));
      setProblem(problemMessage(p));
    } finally {
      sending.current = false;
    }
  };

  return (
    <form onSubmit={submit} noValidate className="space-y-4" data-testid="create-ticket-form">
      <div>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("description")}</DialogDescription>
      </div>
      {problem ? <Alert variant="danger">{problem}</Alert> : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("mobile")} htmlFor="ct-mobile" required error={errorText("mobile")} hint={t("mobileHint")}>
          <Input id="ct-mobile" inputMode="tel" autoComplete="off" value={form.mobile} onChange={(e) => set("mobile", e.target.value)} aria-invalid={!!errors.mobile} />
        </Field>
        <Field label={t("name")} htmlFor="ct-name" error={errorText("customerName")}>
          <Input id="ct-name" autoComplete="off" value={form.customerName} onChange={(e) => set("customerName", e.target.value)} />
        </Field>
        <Field label={t("email")} htmlFor="ct-email" error={errorText("email")}>
          <Input id="ct-email" type="email" autoComplete="off" value={form.email} onChange={(e) => set("email", e.target.value)} />
        </Field>
        <Field label={t("order")} htmlFor="ct-order" hint={t("orderHint")}>
          <Input id="ct-order" autoComplete="off" value={form.orderNumber} onChange={(e) => set("orderNumber", e.target.value)} className="font-mono" />
        </Field>
        <Field label={t("channel")} htmlFor="ct-channel">
          <Select id="ct-channel" value={form.channel} onValueChange={(v) => set("channel", v)} options={STAFF_CHANNELS.map((c) => ({ value: c, label: tch(c) }))} placeholder={t("channel")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <Field label={t("priority")} htmlFor="ct-priority">
          <Select id="ct-priority" value={form.priority} onValueChange={(v) => set("priority", v)} options={TICKET_PRIORITIES.map((p) => ({ value: p, label: tp(p) }))} placeholder={t("priority")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <Field label={t("category")} htmlFor="ct-category">
          <Select id="ct-category" value={form.category} onValueChange={(v) => set("category", v)} options={categories} placeholder={t("category")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        {perms.assign ? (
          <Field label={t("assignee")} htmlFor="ct-assignee" hint={t("assigneeHint")}>
            <Select id="ct-assignee" value={assignee} onValueChange={setAssignee} options={[{ value: "", label: t("noAssignee") }, ...assignees]} placeholder={t("noAssignee")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        ) : null}
      </div>

      <Field label={t("subject")} htmlFor="ct-subject" required error={errorText("subject")}>
        <Input id="ct-subject" value={form.subject} maxLength={SUBJECT_MAX} onChange={(e) => set("subject", e.target.value)} aria-invalid={!!errors.subject} />
      </Field>
      <Field label={t("message")} htmlFor="ct-message" required error={errorText("message")} hint={t("messageHint")}>
        <Textarea id="ct-message" value={form.message} maxLength={MESSAGE_MAX} onChange={(e) => set("message", e.target.value)} className="min-h-32" aria-invalid={!!errors.message} />
      </Field>
      <TicketAttachments scope="staff" idBase="ct" added={files} onAdded={setFiles} onError={(e) => setProblem(problemMessage(e instanceof ApiError ? e.problem : null))} disabled={create.isPending} />

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button variant="secondary" disabled={create.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={create.isPending} data-testid="create-ticket-submit">
          {t("submit")}
        </Button>
      </div>
    </form>
  );
}
