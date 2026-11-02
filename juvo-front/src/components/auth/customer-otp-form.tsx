"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { api } from "@/lib/api/client";
import { postJson } from "@/lib/api/post";
import { toProblem } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { safeNext } from "@/lib/utils";
import { hardNavigate } from "@/lib/navigation";

/**
 * Customer sign-in: mobile number, then the 6-digit code sent by SMS. Works against the real endpoints; in this phase the SMS
 * goes to the sandbox sender (the notification log) until a provider is connected in Step 14.
 */
export function CustomerOtpForm({ next }: { next?: string }) {
  const t = useTranslations("auth");
  const message = useProblemMessage();
  const [step, setStep] = React.useState<"mobile" | "code">("mobile");
  const [mobile, setMobile] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [wait, setWait] = React.useState(0);

  React.useEffect(() => {
    if (wait <= 0) return;
    const id = setTimeout(() => setWait((w) => w - 1), 1000);
    return () => clearTimeout(id);
  }, [wait]);

  const schema = React.useMemo(
    () => z.object({ mobile: z.string().trim().min(5, t("errors.mobileRequired")), code: z.string().trim().optional() }),
    [t],
  );
  type Values = z.infer<typeof schema>;
  const { register, handleSubmit, setError: setFieldError, setFocus, formState } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { mobile: "", code: "" },
  });

  const requestCode = async (value: string) => {
    const { data, error: failure, response } = await api.POST("/api/v1/auth/otp/request", { body: { mobile: value, purpose: null } });
    if (failure || !data) {
      const problem = toProblem(failure, response.status);
      setError(message(problem));
      return false;
    }
    setMobile(data.mobile);
    setWait(Number(data.resendAfterSeconds)); // the API schema allows number or string for int32
    return true;
  };

  const onSubmit = handleSubmit(async (values) => {
    setError(null);
    if (step === "mobile") {
      if (await requestCode(values.mobile)) {
        setStep("code");
        setTimeout(() => setFocus("code"), 0);
      }
      return;
    }
    if (!values.code) {
      setFieldError("code", { message: t("errors.codeRequired") });
      return;
    }
    const result = await postJson("/api/bff/auth/otp/verify", { mobile, code: values.code });
    if (result.ok) {
      hardNavigate(safeNext(next, "/account"));
      return;
    }
    setError(message(result.problem));
  });

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4" aria-label={t("customerTab")}>
      {error ? <Alert variant="danger">{error}</Alert> : null}
      {step === "code" ? <Alert variant="success">{t("codeSent", { mobile })}</Alert> : null}

      <Field label={t("mobile")} htmlFor="mobile" hint={t("mobileHint")} error={formState.errors.mobile?.message} required>
        <Input id="mobile" type="tel" inputMode="tel" autoComplete="tel" readOnly={step === "code"} aria-invalid={!!formState.errors.mobile} {...register("mobile")} />
      </Field>

      {step === "code" ? (
        <Field label={t("otpCode")} htmlFor="code" hint={t("otpHint")} error={formState.errors.code?.message} required>
          <Input id="code" inputMode="numeric" autoComplete="one-time-code" maxLength={8} aria-invalid={!!formState.errors.code} {...register("code")} />
        </Field>
      ) : null}

      <Button type="submit" size="lg" className="w-full" loading={formState.isSubmitting}>
        {step === "mobile" ? t("sendCode") : t("verify")}
      </Button>

      {step === "code" ? (
        <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
          <button type="button" className="tap rounded-control px-1 font-semibold text-primary underline-offset-4 hover:underline" onClick={() => { setStep("mobile"); setError(null); }}>
            {t("changeNumber")}
          </button>
          <button
            type="button"
            disabled={wait > 0}
            className="tap rounded-control px-1 font-semibold text-primary underline-offset-4 hover:underline disabled:text-muted disabled:no-underline"
            onClick={async () => { setError(null); await requestCode(mobile); }}
          >
            {wait > 0 ? t("resendIn", { seconds: wait }) : t("resend")}
          </button>
        </div>
      ) : null}
    </form>
  );
}
