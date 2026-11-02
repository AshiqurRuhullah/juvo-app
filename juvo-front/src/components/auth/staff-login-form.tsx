"use client";

import * as React from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useTranslations } from "next-intl";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { Field, Input, PasswordInput } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { postJson } from "@/lib/api/post";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { safeNext } from "@/lib/utils";
import { hardNavigate } from "@/lib/navigation";

type TokenInfo = { subjectType: string; requiresPasswordChange: boolean; requiresTwoFactorSetup: boolean };

/**
 * Staff sign-in: email or user name + password, then the authenticator code (or a recovery code) when the account has two-step
 * verification. Tokens are set in httpOnly cookies by the server; this form never sees them.
 */
export function StaffLoginForm({ next }: { next?: string }) {
  const t = useTranslations("auth");
  const tc = useTranslations("common");
  const message = useProblemMessage();
  const [step, setStep] = React.useState<"credentials" | "code">("credentials");
  const [recovery, setRecovery] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  const schema = React.useMemo(
    () =>
      z.object({
        identifier: z.string().trim().min(1, t("errors.identifierRequired")),
        password: z.string().min(1, t("errors.passwordRequired")),
        code: z.string().optional(),
      }),
    [t],
  );
  type Values = z.infer<typeof schema>;

  const { register, handleSubmit, setError: setFieldError, setFocus, formState } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { identifier: "", password: "", code: "" },
  });

  const onSubmit = handleSubmit(async (values) => {
    setError(null);
    if (step === "code" && !values.code?.trim()) {
      setFieldError("code", { message: t("errors.codeRequired") });
      return;
    }
    const code = values.code?.trim();
    const result = await postJson<TokenInfo>("/api/bff/auth/login", {
      identifier: values.identifier,
      password: values.password,
      totpCode: step === "code" && !recovery ? code : undefined,
      recoveryCode: step === "code" && recovery ? code : undefined,
    });

    if (result.ok) {
      const flags = result.data;
      const target = flags.requiresPasswordChange || flags.requiresTwoFactorSetup ? "/first-login" : safeNext(next, "/admin");
      hardNavigate(target); // full navigation so the new cookies are used by the first server render
      return;
    }

    if (result.problem.code === "AUTH.TWO_FACTOR_REQUIRED") {
      setStep("code");
      setTimeout(() => setFocus("code"), 0);
      return;
    }
    setError(message(result.problem));
  });

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4" aria-label={t("staffTab")}>
      {error ? <Alert variant="danger">{error}</Alert> : null}

      <Field label={t("identifier")} htmlFor="identifier" error={formState.errors.identifier?.message} required>
        <Input id="identifier" autoComplete="username" autoCapitalize="none" spellCheck={false} inputMode="email" readOnly={step === "code"} aria-invalid={!!formState.errors.identifier} {...register("identifier")} />
      </Field>

      <Field label={t("password")} htmlFor="password" error={formState.errors.password?.message} required>
        <PasswordInput id="password" autoComplete="current-password" readOnly={step === "code"} showLabel={t("showPassword")} hideLabel={t("hidePassword")} aria-invalid={!!formState.errors.password} {...register("password")} />
      </Field>

      {step === "code" ? (
        <div className="space-y-2">
          <Field
            label={recovery ? t("recoveryCode") : t("authenticatorCode")}
            htmlFor="code"
            hint={recovery ? t("recoveryHint") : t("authenticatorHint")}
            error={formState.errors.code?.message}
            required
          >
            <Input
              id="code"
              autoComplete="one-time-code"
              inputMode={recovery ? "text" : "numeric"}
              autoCapitalize="none"
              spellCheck={false}
              aria-invalid={!!formState.errors.code}
              {...register("code")}
            />
          </Field>
          <button type="button" className="tap -mx-1 rounded-control px-1 text-sm font-semibold text-primary underline-offset-4 hover:underline" onClick={() => setRecovery((r) => !r)}>
            {recovery ? t("useAuthenticator") : t("useRecovery")}
          </button>
        </div>
      ) : null}

      <Button type="submit" size="lg" className="w-full" loading={formState.isSubmitting}>
        {step === "code" ? t("verify") : t("signIn")}
      </Button>

      <p className="text-center">
        <Link href="/forgot-password" className="tap inline-flex items-center rounded-control px-2 text-sm font-semibold text-primary underline-offset-4 hover:underline">
          {t("forgotPassword")}
        </Link>
      </p>
      <span className="sr-only">{tc("loading")}</span>
    </form>
  );
}
