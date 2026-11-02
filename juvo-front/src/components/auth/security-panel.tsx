"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { KeyRound, ShieldCheck, ShieldOff } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, PasswordInput } from "@/components/ui/form";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { api } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { ChangePasswordForm } from "./password-forms";
import { TwoFactorSetup } from "./two-factor";

/** Password and two-step verification of the signed-in staff member (profile page). */
export function SecurityPanel({ twoFactorEnabled, mandatoryTwoFactor }: { twoFactorEnabled: boolean; mandatoryTwoFactor: boolean }) {
  const t = useTranslations("security");
  const router = useRouter();

  return (
    <div className="grid gap-4 xl:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <KeyRound className="size-6 text-primary" aria-hidden /> {t("passwordTitle")}
          </CardTitle>
          <CardDescription>{t("passwordHelp")}</CardDescription>
        </CardHeader>
        <CardContent>
          <ChangePasswordForm
            onChanged={() => {
              toast.success(t("passwordChanged"));
              router.refresh();
            }}
          />
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            {twoFactorEnabled ? <ShieldCheck className="size-6 text-success" aria-hidden /> : <ShieldOff className="size-6 text-muted" aria-hidden />}
            {t("twoFactorTitle")}
          </CardTitle>
          <CardDescription>{twoFactorEnabled ? t("twoFactorOn") : t("twoFactorOff")}</CardDescription>
        </CardHeader>
        <CardContent>
          {twoFactorEnabled ? (
            <DisableTwoFactor mandatory={mandatoryTwoFactor} onDisabled={() => router.refresh()} />
          ) : (
            <TwoFactorSetup onEnabled={() => router.refresh()} />
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function DisableTwoFactor({ mandatory, onDisabled }: { mandatory: boolean; onDisabled: () => void }) {
  const t = useTranslations("security");
  const ta = useTranslations("auth");
  const message = useProblemMessage();
  const [password, setPassword] = React.useState("");
  const [code, setCode] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [codes, setCodes] = React.useState<string[] | null>(null);

  if (mandatory) return <Alert variant="info">{t("mandatory")}</Alert>;

  const disable = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const { error: failure, response } = await api.POST("/api/v1/auth/2fa/disable", { body: { password, code: code.trim() } });
    setBusy(false);
    if (failure) setError(message(toProblem(failure, response.status)));
    else {
      toast.success(t("disabled"));
      onDisabled();
    }
  };

  const regenerate = async () => {
    setError(null);
    const { data, error: failure, response } = await api.POST("/api/v1/auth/2fa/recovery-codes");
    if (failure || !data) setError(message(toProblem(failure, response.status)));
    else setCodes(data.recoveryCodes);
  };

  return (
    <div className="space-y-5">
      {error ? <Alert variant="danger">{error}</Alert> : null}
      {codes ? (
        <div className="space-y-2">
          <Alert variant="warning">{t("newRecoveryCodes")}</Alert>
          <ul className="grid grid-cols-2 gap-2 rounded-card border border-line bg-sunken p-4 font-mono text-sm" aria-label={t("recoveryCodes")}>
            {codes.map((c) => (
              <li key={c} className="select-all text-center">
                {c}
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <Button variant="secondary" onClick={regenerate}>
          {t("regenerate")}
        </Button>
      )}
      <form onSubmit={disable} className="space-y-4 border-t border-line pt-5" noValidate>
        <p className="font-semibold text-strong">{t("disableTitle")}</p>
        <Field label={ta("currentPassword")} htmlFor="disable-password" required>
          <PasswordInput id="disable-password" autoComplete="current-password" showLabel={ta("showPassword")} hideLabel={ta("hidePassword")} value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Field label={ta("authenticatorCode")} htmlFor="disable-code" required>
          <Input id="disable-code" inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value)} />
        </Field>
        <Button type="submit" variant="danger" loading={busy} disabled={!password || code.trim().length < 6}>
          {t("disable")}
        </Button>
      </form>
    </div>
  );
}
