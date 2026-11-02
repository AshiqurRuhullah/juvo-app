"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Field, Input, PasswordInput } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { api } from "@/lib/api/client";
import { fieldErrors, toProblem } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { postJson } from "@/lib/api/post";

/** The password rules the API enforces, repeated here so the user sees the problem before sending. */
export function usePasswordSchema() {
  const t = useTranslations("auth.errors");
  return React.useMemo(
    () =>
      z
        .string()
        .min(10, t("passwordShort"))
        .regex(/[a-z]/, t("passwordLower"))
        .regex(/[A-Z]/, t("passwordUpper"))
        .regex(/\d/, t("passwordDigit"))
        .regex(/[^A-Za-z0-9]/, t("passwordSymbol")),
    [t],
  );
}

export function PasswordRules() {
  const t = useTranslations("auth");
  return <p className="text-sm text-muted">{t("passwordRules")}</p>;
}

export function ForgotPasswordForm() {
  const t = useTranslations("auth");
  const message = useProblemMessage();
  const [sent, setSent] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const schema = React.useMemo(() => z.object({ email: z.string().trim().email(t("errors.emailInvalid")) }), [t]);
  const { register, handleSubmit, formState } = useForm<z.infer<typeof schema>>({ resolver: zodResolver(schema), defaultValues: { email: "" } });

  const onSubmit = handleSubmit(async ({ email }) => {
    setError(null);
    const { error: failure, response } = await api.POST("/api/v1/auth/forgot-password", { body: { email } });
    if (failure) setError(message(toProblem(failure, response.status)));
    else setSent(true);
  });

  if (sent) {
    return (
      <div className="space-y-4">
        <Alert variant="success" title={t("resetSentTitle")}>
          {t("resetSentBody")}
        </Alert>
        <Button asChild variant="secondary" className="w-full">
          <Link href="/login">{t("backToSignIn")}</Link>
        </Button>
      </div>
    );
  }

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4">
      {error ? <Alert variant="danger">{error}</Alert> : null}
      <p className="text-muted">{t("forgotBody")}</p>
      <Field label={t("email")} htmlFor="email" error={formState.errors.email?.message} required>
        <Input id="email" type="email" autoComplete="email" inputMode="email" autoCapitalize="none" aria-invalid={!!formState.errors.email} {...register("email")} />
      </Field>
      <Button type="submit" size="lg" className="w-full" loading={formState.isSubmitting}>
        {t("sendResetLink")}
      </Button>
      <Button asChild variant="ghost" className="w-full">
        <Link href="/login">{t("backToSignIn")}</Link>
      </Button>
    </form>
  );
}

export function ResetPasswordForm({ email, token }: { email: string; token: string }) {
  const t = useTranslations("auth");
  const message = useProblemMessage();
  const passwordSchema = usePasswordSchema();
  const [error, setError] = React.useState<string | null>(null);
  const [done, setDone] = React.useState(false);

  const schema = React.useMemo(
    () =>
      z
        .object({ newPassword: passwordSchema, confirm: z.string() })
        .refine((v) => v.newPassword === v.confirm, { path: ["confirm"], message: t("errors.passwordMismatch") }),
    [passwordSchema, t],
  );
  const { register, handleSubmit, setError: setFieldError, formState } = useForm<z.infer<typeof schema>>({ resolver: zodResolver(schema), defaultValues: { newPassword: "", confirm: "" } });

  const onSubmit = handleSubmit(async ({ newPassword }) => {
    setError(null);
    const { error: failure, response } = await api.POST("/api/v1/auth/reset-password", { body: { email, token, newPassword } });
    if (!failure) {
      setDone(true);
      return;
    }
    const problem = toProblem(failure, response.status);
    const fields = fieldErrors(problem);
    if (fields.password) setFieldError("newPassword", { message: fields.password });
    else setError(message(problem));
  });

  if (done) {
    return (
      <div className="space-y-4">
        <Alert variant="success">{t("resetDone")}</Alert>
        <Button asChild size="lg" className="w-full">
          <Link href="/login?reason=password-changed">{t("signIn")}</Link>
        </Button>
      </div>
    );
  }

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4">
      {error ? <Alert variant="danger">{error}</Alert> : null}
      <p className="text-muted">{t("resetBody", { email })}</p>
      <Field label={t("newPassword")} htmlFor="newPassword" error={formState.errors.newPassword?.message} required>
        <PasswordInput id="newPassword" autoComplete="new-password" showLabel={t("showPassword")} hideLabel={t("hidePassword")} aria-invalid={!!formState.errors.newPassword} {...register("newPassword")} />
      </Field>
      <PasswordRules />
      <Field label={t("confirmPassword")} htmlFor="confirm" error={formState.errors.confirm?.message} required>
        <PasswordInput id="confirm" autoComplete="new-password" showLabel={t("showPassword")} hideLabel={t("hidePassword")} aria-invalid={!!formState.errors.confirm} {...register("confirm")} />
      </Field>
      <Button type="submit" size="lg" className="w-full" loading={formState.isSubmitting}>
        {t("setPassword")}
      </Button>
    </form>
  );
}

/** Change password for the signed-in user (profile page and the forced first-login step). */
export function ChangePasswordForm({ onChanged, requireCurrent = true }: { onChanged: (flags: { requiresTwoFactorSetup: boolean }) => void; requireCurrent?: boolean }) {
  const t = useTranslations("auth");
  const message = useProblemMessage();
  const passwordSchema = usePasswordSchema();
  const [error, setError] = React.useState<string | null>(null);

  const schema = React.useMemo(
    () =>
      z
        .object({ currentPassword: z.string().min(1, t("errors.passwordRequired")), newPassword: passwordSchema, confirm: z.string() })
        .refine((v) => v.newPassword === v.confirm, { path: ["confirm"], message: t("errors.passwordMismatch") })
        .refine((v) => v.newPassword !== v.currentPassword, { path: ["newPassword"], message: t("errors.passwordSame") }),
    [passwordSchema, t],
  );
  const { register, handleSubmit, setError: setFieldError, reset, formState } = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    defaultValues: { currentPassword: "", newPassword: "", confirm: "" },
  });

  const onSubmit = handleSubmit(async ({ currentPassword, newPassword }) => {
    setError(null);
    const result = await postJson<{ requiresTwoFactorSetup: boolean }>("/api/bff/auth/change-password", { currentPassword, newPassword });
    if (result.ok) {
      reset();
      onChanged({ requiresTwoFactorSetup: result.data.requiresTwoFactorSetup });
      return;
    }
    const fields = fieldErrors(result.problem);
    if (result.problem.code === "AUTH.INVALID_CREDENTIALS") setFieldError("currentPassword", { message: t("errors.currentWrong") });
    else if (fields.password) setFieldError("newPassword", { message: fields.password });
    else setError(message(result.problem));
  });

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4">
      {error ? <Alert variant="danger">{error}</Alert> : null}
      <Field label={requireCurrent ? t("currentPassword") : t("temporaryPassword")} htmlFor="currentPassword" error={formState.errors.currentPassword?.message} required>
        <PasswordInput id="currentPassword" autoComplete="current-password" showLabel={t("showPassword")} hideLabel={t("hidePassword")} aria-invalid={!!formState.errors.currentPassword} {...register("currentPassword")} />
      </Field>
      <Field label={t("newPassword")} htmlFor="newPassword" error={formState.errors.newPassword?.message} required>
        <PasswordInput id="newPassword" autoComplete="new-password" showLabel={t("showPassword")} hideLabel={t("hidePassword")} aria-invalid={!!formState.errors.newPassword} {...register("newPassword")} />
      </Field>
      <PasswordRules />
      <Field label={t("confirmPassword")} htmlFor="confirm" error={formState.errors.confirm?.message} required>
        <PasswordInput id="confirm" autoComplete="new-password" showLabel={t("showPassword")} hideLabel={t("hidePassword")} aria-invalid={!!formState.errors.confirm} {...register("confirm")} />
      </Field>
      <Button type="submit" size="lg" loading={formState.isSubmitting}>
        {t("changePassword")}
      </Button>
    </form>
  );
}
