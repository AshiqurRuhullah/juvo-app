"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input, Switch } from "@/components/ui/form";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useCartErrorText, useReportCartError } from "@/modules/cart/use-cart-error";
import { useConsents, useMe, useSetConsent, useUpdateProfile, type CustomerProfile } from "@/modules/store/api";

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

function ProfileForm({ profile }: { profile: CustomerProfile }) {
  const t = useTranslations("myaccount.profile");
  const update = useUpdateProfile();
  const report = useReportCartError();
  const [fullName, setFullName] = React.useState(profile.fullName ?? "");
  const [email, setEmail] = React.useState(profile.email ?? "");
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    if (email.trim() && !EMAIL.test(email.trim())) next.email = t("emailInvalid");
    setErrors(next);
    if (Object.keys(next).length) return;
    try {
      await update.mutateAsync({ fullName: fullName.trim() || null, email: email.trim() || null });
      toast.success(t("saved"));
    } catch (failure) {
      setErrors(report(failure));
    }
  };

  return (
    <form onSubmit={submit} noValidate className="space-y-4">
      <Field label={t("mobile")} htmlFor="profile-mobile" hint={t("mobileHint")}>
        <Input id="profile-mobile" value={profile.mobile} readOnly aria-describedby="profile-mobile-hint" className="bg-sunken" />
      </Field>
      <Field label={t("name")} htmlFor="profile-name" error={errors.fullName}>
        <Input id="profile-name" value={fullName} onChange={(e) => setFullName(e.target.value)} autoComplete="name" maxLength={150} />
      </Field>
      <Field label={t("email")} htmlFor="profile-email" hint={t("emailHint")} error={errors.email}>
        <Input id="profile-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" inputMode="email" aria-invalid={!!errors.email} aria-describedby={errors.email ? "profile-email-error" : "profile-email-hint"} />
      </Field>
      <Button type="submit" loading={update.isPending}>
        {t("save")}
      </Button>
    </form>
  );
}

const CONSENTS = [
  { type: "MarketingSms", key: "sms" },
  { type: "MarketingEmail", key: "email" },
  { type: "PrivacyPolicy", key: "privacy" },
] as const;

function Consents() {
  const t = useTranslations("myaccount.consent");
  const format = useFormatter();
  const consents = useConsents();
  const set = useSetConsent();
  const errorText = useCartErrorText();

  if (consents.isPending) return <Skeleton className="h-32" />;
  if (consents.isError) return <Alert variant="danger">{errorText(consents.error)}</Alert>;

  const change = async (type: (typeof CONSENTS)[number]["type"], granted: boolean) => {
    try {
      await set.mutateAsync({ type, granted });
      toast.success(t("saved"));
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  return (
    <ul className="divide-y divide-line">
      {CONSENTS.map(({ type, key }) => {
        const row = consents.data.find((c) => c.type === type);
        const id = `consent-${key}`;
        return (
          <li key={type} className="flex items-start justify-between gap-4 py-3">
            <div className="min-w-0">
              <label htmlFor={id} className="block cursor-pointer font-medium text-strong">
                {t(`${key}.title`)}
              </label>
              <p className="text-sm text-muted">{t(`${key}.text`)}</p>
              <p className="text-xs text-muted">{row?.at ? t("since", { date: format.dateTime(new Date(row.at), { dateStyle: "medium", timeStyle: "short" }) }) : t("never")}</p>
            </div>
            <Switch id={id} checked={!!row?.granted} disabled={set.isPending} onCheckedChange={(v) => void change(type, v)} className="mt-1" />
          </li>
        );
      })}
    </ul>
  );
}

export function ProfileView() {
  const t = useTranslations("myaccount.profile");
  const tc = useTranslations("myaccount.consent");
  const me = useMe();
  const errorText = useCartErrorText();

  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <CardTitle>{t("title")}</CardTitle>
          <CardDescription>{t("help")}</CardDescription>
        </CardHeader>
        <CardContent>
          {me.isPending ? <Skeleton className="h-56" /> : me.isError ? <Alert variant="danger">{errorText(me.error)}</Alert> : <ProfileForm profile={me.data} />}
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>{tc("title")}</CardTitle>
          <CardDescription>{tc("help")}</CardDescription>
        </CardHeader>
        <CardContent>
          <Consents />
        </CardContent>
      </Card>
    </div>
  );
}
