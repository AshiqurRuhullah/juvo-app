"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { CircleCheck } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useSendContactMessage } from "./api";
import { LIMITS, emptyContact, hasErrors, sendFailure, validateContact, type ContactErrors, type ContactFields } from "./logic";

/**
 * The public contact form (works signed out). Checks run in the browser first and the API checks again. The hidden `website` field is a
 * trap for bots: it is moved out of sight, unreachable by keyboard and screen reader (`inert`), so a person never fills it.
 * Success shows the reference the API returned.
 */
export function ContactMessageForm() {
  const t = useTranslations("contact.public");
  const send = useSendContactMessage();
  const [form, setForm] = React.useState<ContactFields>(emptyContact);
  const [errors, setErrors] = React.useState<ContactErrors>({});
  const [serverErrors, setServerErrors] = React.useState<Record<string, string>>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const [reference, setReference] = React.useState<string | null>(null);
  const honeypot = React.useRef<HTMLInputElement>(null);
  const sending = React.useRef(false);

  const set = <K extends keyof ContactFields>(key: K, value: string) => setForm((f) => ({ ...f, [key]: value }));
  const errorText = (key: keyof ContactFields) => {
    const code = errors[key];
    if (code) return t(`errors.${code}`, { max: key === "name" ? LIMITS.name : key === "subject" ? LIMITS.subject : LIMITS.messageMax, min: LIMITS.messageMin });
    return serverErrors[key];
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (sending.current) return;
    const found = validateContact(form);
    setErrors(found);
    setServerErrors({});
    setFailure(null);
    if (hasErrors(found)) return;
    sending.current = true;
    try {
      const receipt = await send.mutateAsync({ form, honeypot: honeypot.current?.value ?? "" });
      setReference(receipt.reference);
    } catch (e) {
      const problem = e instanceof ApiError ? e.problem : null;
      if (problem?.status === 400 && problem.errors) setServerErrors(fieldErrors(problem));
      setFailure(t(`failures.${sendFailure(problem?.status, problem?.code)}`));
    } finally {
      sending.current = false;
    }
  };

  if (reference) {
    return (
      <div className="space-y-4 rounded-card border border-line bg-card p-6" data-testid="contact-sent" role="status">
        <p className="flex items-center gap-2 font-display text-xl font-semibold text-strong">
          <CircleCheck className="size-6 shrink-0 text-success" aria-hidden /> {t("done.title")}
        </p>
        <p className="text-strong">
          {t("done.reference")}: <span className="font-mono font-semibold" data-testid="contact-reference">{reference}</span>
        </p>
        <p className="text-muted">{t("done.help")}</p>
        <div className="flex flex-wrap gap-2">
          <Button
            variant="secondary"
            onClick={() => {
              setForm(emptyContact());
              setReference(null);
            }}
          >
            {t("done.another")}
          </Button>
          <Link href="/shop" className={buttonVariants({ variant: "ghost" })}>
            {t("done.shop")}
          </Link>
        </div>
      </div>
    );
  }

  return (
    <form onSubmit={submit} noValidate className="space-y-4" aria-label={t("formLabel")} data-testid="contact-message-form">
      {failure ? (
        <Alert variant="danger" data-testid="contact-failure">
          {failure}
        </Alert>
      ) : null}
      <Field label={t("name")} htmlFor="cm-name" required error={errorText("name")}>
        <Input id="cm-name" value={form.name} maxLength={LIMITS.name} autoComplete="name" onChange={(e) => set("name", e.target.value)} aria-invalid={!!errorText("name")} />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("mobile")} htmlFor="cm-mobile" error={errorText("mobile")} hint={t("mobileHint")}>
          <Input id="cm-mobile" inputMode="tel" autoComplete="tel" value={form.mobile} onChange={(e) => set("mobile", e.target.value)} aria-invalid={!!errorText("mobile")} />
        </Field>
        <Field label={t("email")} htmlFor="cm-email" error={errorText("email")} hint={t("emailHint")}>
          <Input id="cm-email" type="email" autoComplete="email" value={form.email} onChange={(e) => set("email", e.target.value)} aria-invalid={!!errorText("email")} />
        </Field>
      </div>
      <Field label={t("subject")} htmlFor="cm-subject" error={errorText("subject")}>
        <Input id="cm-subject" value={form.subject} maxLength={LIMITS.subject} onChange={(e) => set("subject", e.target.value)} aria-invalid={!!errorText("subject")} />
      </Field>
      <Field label={t("message")} htmlFor="cm-message" required error={errorText("message")} hint={t("messageHint")}>
        <Textarea id="cm-message" value={form.message} maxLength={LIMITS.messageMax} onChange={(e) => set("message", e.target.value)} className="min-h-40" aria-invalid={!!errorText("message")} />
      </Field>
      {/* Bot trap: out of sight, not focusable, hidden from assistive technology. Real visitors never fill it. */}
      <div aria-hidden="true" inert className="pointer-events-none absolute left-[-9999px] top-auto size-px overflow-hidden" data-testid="contact-honeypot-wrap">
        <label htmlFor="cm-website">Website</label>
        <input ref={honeypot} id="cm-website" name="website" type="text" tabIndex={-1} autoComplete="off" defaultValue="" data-testid="contact-honeypot" />
      </div>
      <Button type="submit" loading={send.isPending} data-testid="contact-message-submit">
        {t("send")}
      </Button>
    </form>
  );
}
