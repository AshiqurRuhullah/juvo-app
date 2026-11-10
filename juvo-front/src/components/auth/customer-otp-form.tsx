"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { FlaskConical, Mail, MessageSquareText } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, Label, RadioGroup, RadioItem } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { api } from "@/lib/api/client";
import { postJson } from "@/lib/api/post";
import { toProblem } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { safeNext } from "@/lib/utils";
import { hardNavigate } from "@/lib/navigation";

type Channel = "sms" | "email";

/**
 * Customer sign-in: mobile number, then a 6-digit code that goes by SMS or by email (the customer chooses). The mobile number stays
 * the identity. No real SMS or email provider is connected before the go-live step, so the code goes to the sandbox senders; in
 * sandbox mode (Development) the API also returns the code and this form shows it, so nobody is left waiting for a message that
 * will not come. In production the API never returns the code and the notice never appears.
 */
export function CustomerOtpForm({ next }: { next?: string }) {
  const t = useTranslations("auth");
  const o = useTranslations("otp");
  const message = useProblemMessage();
  const [step, setStep] = React.useState<"details" | "code">("details");
  const [channel, setChannel] = React.useState<Channel>("sms");
  const [sent, setSent] = React.useState<{ mobile: string; email: string; to: string; channel: Channel } | null>(null);
  const [devCode, setDevCode] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [wait, setWait] = React.useState(0);

  React.useEffect(() => {
    if (wait <= 0) return;
    const id = setTimeout(() => setWait((w) => w - 1), 1000);
    return () => clearTimeout(id);
  }, [wait]);

  const schema = React.useMemo(
    () => z.object({ mobile: z.string().trim().min(5, t("errors.mobileRequired")), email: z.string().trim().optional(), code: z.string().trim().optional() }),
    [t],
  );
  type Values = z.infer<typeof schema>;
  const { register, handleSubmit, setError: setFieldError, setFocus, formState } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { mobile: "", email: "", code: "" },
  });

  const requestCode = async (mobile: string, email: string) => {
    const { data, error: failure, response } = await api.POST("/api/v1/auth/otp/request", {
      body: { mobile, purpose: null, channel, email: channel === "email" ? email : null },
    });
    if (failure || !data) {
      setError(message(toProblem(failure, response.status)));
      return false;
    }
    setSent({ mobile: data.mobile, email, to: data.sentTo, channel: data.channel === "email" ? "email" : "sms" });
    setDevCode(data.devCode ?? null);
    setWait(Number(data.resendAfterSeconds)); // the API schema allows number or string for int32
    return true;
  };

  const onSubmit = handleSubmit(async (values) => {
    setError(null);
    if (step === "details") {
      if (channel === "email" && !/^\S+@\S+\.\S+$/.test(values.email ?? "")) {
        setFieldError("email", { message: o("emailInvalid") });
        return;
      }
      if (await requestCode(values.mobile, values.email ?? "")) {
        setStep("code");
        setTimeout(() => setFocus("code"), 0);
      }
      return;
    }
    if (!values.code) {
      setFieldError("code", { message: t("errors.codeRequired") });
      return;
    }
    const result = await postJson("/api/bff/auth/otp/verify", { mobile: sent?.mobile ?? values.mobile, code: values.code, email: sent?.channel === "email" ? sent.email : undefined });
    if (result.ok) {
      hardNavigate(safeNext(next, "/account"));
      return;
    }
    setError(message(result.problem));
  });

  const locked = step === "code";

  return (
    <form onSubmit={onSubmit} noValidate className="space-y-4" aria-label={t("customerTab")}>
      {error ? <Alert variant="danger">{error}</Alert> : null}
      {step === "code" && sent ? <Alert variant="success">{sent.channel === "email" ? o("sentEmail", { to: sent.to }) : o("sentSms", { to: sent.to })}</Alert> : null}
      {step === "code" && devCode ? (
        <div role="note" className="rounded-control border border-line bg-primary-soft p-4" data-testid="sandbox-code">
          <p className="flex items-center gap-2 text-sm font-semibold text-strong">
            <FlaskConical className="size-5 shrink-0 text-primary" aria-hidden /> {o("sandboxTitle")}
          </p>
          <p className="mt-2 text-sm text-muted">{o("sandboxBody")}</p>
          <p className="mt-1 text-center text-3xl font-bold tracking-[0.3em] text-strong" aria-label={o("sandboxCodeLabel", { code: devCode.split("").join(" ") })}>
            {devCode}
          </p>
          <p className="mt-2 text-xs text-muted">{o("sandboxHelp")}</p>
        </div>
      ) : null}

      <Field label={t("mobile")} htmlFor="mobile" hint={t("mobileHint")} error={formState.errors.mobile?.message} required>
        <Input id="mobile" type="tel" inputMode="tel" autoComplete="tel" readOnly={locked} aria-invalid={!!formState.errors.mobile} {...register("mobile")} />
      </Field>

      <fieldset className="space-y-2" disabled={locked}>
        <legend className="text-sm font-semibold text-strong">{o("channelLabel")}</legend>
        <RadioGroup value={channel} onValueChange={(v) => setChannel(v === "email" ? "email" : "sms")} className="grid grid-cols-2 gap-2" aria-label={o("channelLabel")}>
          {(["sms", "email"] as const).map((value) => {
            const Icon = value === "sms" ? MessageSquareText : Mail;
            return (
              <div key={value} className="flex min-h-12 items-center gap-3 rounded-control border border-line bg-card px-3 has-[[data-state=checked]]:border-primary has-[[data-state=checked]]:bg-primary-soft">
                <RadioItem id={`channel-${value}`} value={value} />
                <Label htmlFor={`channel-${value}`} className="flex min-h-11 flex-1 cursor-pointer items-center gap-2">
                  <Icon className="size-5 text-muted" aria-hidden /> {value === "sms" ? o("viaSms") : o("viaEmail")}
                </Label>
              </div>
            );
          })}
        </RadioGroup>
      </fieldset>

      {channel === "email" ? (
        <Field label={o("emailLabel")} htmlFor="email" hint={o("emailHint")} error={formState.errors.email?.message} required>
          <Input id="email" type="email" inputMode="email" autoComplete="email" readOnly={locked} aria-invalid={!!formState.errors.email} {...register("email")} />
        </Field>
      ) : null}

      {step === "code" ? (
        <Field label={t("otpCode")} htmlFor="code" hint={t("otpHint")} error={formState.errors.code?.message} required>
          <Input id="code" inputMode="numeric" autoComplete="one-time-code" maxLength={8} aria-invalid={!!formState.errors.code} {...register("code")} />
        </Field>
      ) : null}

      <Button type="submit" size="lg" className="w-full" loading={formState.isSubmitting}>
        {step === "details" ? t("sendCode") : t("verify")}
      </Button>

      {step === "code" ? (
        <div className="flex flex-wrap items-center justify-between gap-2 text-sm">
          <button type="button" className="tap rounded-control px-1 font-semibold text-primary underline-offset-4 hover:underline" onClick={() => { setStep("details"); setError(null); setDevCode(null); }}>
            {o("changeDestination")}
          </button>
          <button
            type="button"
            disabled={wait > 0}
            className="tap rounded-control px-1 font-semibold text-primary underline-offset-4 hover:underline disabled:text-muted disabled:no-underline"
            onClick={async () => { setError(null); if (sent) await requestCode(sent.mobile, sent.email); }}
          >
            {wait > 0 ? t("resendIn", { seconds: wait }) : t("resend")}
          </button>
        </div>
      ) : null}
    </form>
  );
}
