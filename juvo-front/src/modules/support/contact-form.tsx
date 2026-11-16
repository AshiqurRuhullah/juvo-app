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
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useSendContact } from "./api";
import { MESSAGE_MAX, NAME_MAX, SUBJECT_MAX, TICKET_CATEGORIES, hasErrors, validateContact, type ContactForm } from "./logic";

const EMPTY: ContactForm = { name: "", mobile: "", email: "", subject: "", message: "", category: "other", orderNumber: "" };

/**
 * The public contact form (works signed out): name, mobile, optional email, topic, subject, message, and optionally an order number
 * (the order is linked only when the mobile number is the one that placed it). Guests cannot read tickets online, so the success
 * screen says the answer comes by SMS or email. Kept light on purpose: no heavy widgets, no data fetched on load.
 */
export function ContactFormView() {
  const t = useTranslations("support.contact");
  const tcat = useTranslations("support.category");
  const problemMessage = useProblemMessage();
  const send = useSendContact();
  const [form, setForm] = React.useState<ContactForm>(EMPTY);
  const [errors, setErrors] = React.useState<ReturnType<typeof validateContact>>({});
  const [serverErrors, setServerErrors] = React.useState<Record<string, string>>({});
  const [problem, setProblem] = React.useState<string | null>(null);
  const [number, setNumber] = React.useState<string | null>(null);
  const sending = React.useRef(false);

  const set = <K extends keyof ContactForm>(key: K, value: ContactForm[K]) => setForm((f) => ({ ...f, [key]: value }));
  const errorText = (key: keyof ContactForm) => {
    const e = (errors as Record<string, string | undefined>)[key];
    return e ? t(`errors.${e}`, { max: key === "message" ? MESSAGE_MAX : key === "subject" ? SUBJECT_MAX : NAME_MAX }) : serverErrors[key];
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (sending.current) return;
    const found = validateContact(form);
    setErrors(found);
    setServerErrors({});
    setProblem(null);
    if (hasErrors(found)) return;
    sending.current = true;
    try {
      const receipt = await send.mutateAsync(form);
      setNumber(receipt.number);
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.status === 400 && p.errors) setServerErrors(fieldErrors(p));
      setProblem(problemMessage(p));
      sending.current = false;
    }
  };

  if (number) {
    return (
      <div className="space-y-4 rounded-card border border-line bg-card p-6" data-testid="contact-done" role="status">
        <p className="flex items-center gap-2 font-display text-xl font-semibold text-strong">
          <CircleCheck className="size-6 text-success" aria-hidden /> {t("doneTitle")}
        </p>
        <p className="text-strong">
          {t("doneNumber")} <span className="font-mono font-semibold" data-testid="contact-number">{number}</span>
        </p>
        <p className="text-muted">{t("doneHelp")}</p>
        <Link href="/shop" className={buttonVariants({ variant: "secondary" })}>
          {t("keepShopping")}
        </Link>
      </div>
    );
  }

  return (
    <form onSubmit={submit} noValidate className="space-y-4" data-testid="contact-form">
      {problem ? <Alert variant="danger">{problem}</Alert> : null}
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("name")} htmlFor="c-name" required error={errorText("name")}>
          <Input id="c-name" value={form.name} maxLength={NAME_MAX} autoComplete="name" onChange={(e) => set("name", e.target.value)} aria-invalid={!!errors.name} />
        </Field>
        <Field label={t("mobile")} htmlFor="c-mobile" required error={errorText("mobile")} hint={t("mobileHint")}>
          <Input id="c-mobile" inputMode="tel" autoComplete="tel" value={form.mobile} onChange={(e) => set("mobile", e.target.value)} aria-invalid={!!errors.mobile} />
        </Field>
        <Field label={t("email")} htmlFor="c-email" error={errorText("email")} hint={t("emailHint")}>
          <Input id="c-email" type="email" autoComplete="email" value={form.email} onChange={(e) => set("email", e.target.value)} aria-invalid={!!errors.email} />
        </Field>
        <Field label={t("category")} htmlFor="c-category">
          <select
            id="c-category"
            value={form.category}
            onChange={(e) => set("category", e.target.value)}
            className="h-12 w-full rounded-control border border-line bg-card px-4 text-base text-strong transition-[border-color,box-shadow] duration-150 focus-visible:border-primary focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-[var(--brand-primary-glow)]"
          >
            {TICKET_CATEGORIES.map((c) => (
              <option key={c} value={c}>
                {tcat(c)}
              </option>
            ))}
          </select>
        </Field>
      </div>
      <Field label={t("subject")} htmlFor="c-subject" required error={errorText("subject")}>
        <Input id="c-subject" value={form.subject} maxLength={SUBJECT_MAX} onChange={(e) => set("subject", e.target.value)} aria-invalid={!!errors.subject} />
      </Field>
      <Field label={t("message")} htmlFor="c-message" required error={errorText("message")}>
        <Textarea id="c-message" value={form.message} maxLength={MESSAGE_MAX} onChange={(e) => set("message", e.target.value)} className="min-h-36" aria-invalid={!!errors.message} />
      </Field>
      <Field label={t("order")} htmlFor="c-order" hint={t("orderHint")}>
        <Input id="c-order" value={form.orderNumber} autoComplete="off" onChange={(e) => set("orderNumber", e.target.value)} className="font-mono" />
      </Field>
      <p className="text-sm text-muted">{t("noFiles")}</p>
      <Button type="submit" loading={send.isPending} data-testid="contact-submit">
        {t("submit")}
      </Button>
    </form>
  );
}
