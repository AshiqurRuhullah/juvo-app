"use client";

import * as React from "react";
import QRCode from "qrcode";
import { useTranslations } from "next-intl";
import { Copy, ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { api } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";

/**
 * Two-step verification setup: scan the QR code (or type the key) in an authenticator app, confirm with a code, and keep the
 * ten recovery codes that are shown once. Used on the profile page and in the forced first-login flow.
 */
export function TwoFactorSetup({ onEnabled }: { onEnabled: () => void }) {
  const t = useTranslations("twoFactor");
  const message = useProblemMessage();
  const [setup, setSetup] = React.useState<{ sharedKey: string; authenticatorUri: string } | null>(null);
  const [qr, setQr] = React.useState<string | null>(null);
  const [code, setCode] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [recovery, setRecovery] = React.useState<string[] | null>(null);

  const begin = async () => {
    setBusy(true);
    setError(null);
    const { data, error: failure, response } = await api.POST("/api/v1/auth/2fa/setup");
    setBusy(false);
    if (failure || !data) {
      setError(message(toProblem(failure, response.status)));
      return;
    }
    setSetup(data);
    setQr(await QRCode.toDataURL(data.authenticatorUri, { margin: 1, width: 224, color: { dark: "#1C0807", light: "#FFFFFF" } }));
  };

  const enable = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    const { data, error: failure, response } = await api.POST("/api/v1/auth/2fa/enable", { body: { code: code.trim() } });
    setBusy(false);
    if (failure || !data) {
      setError(message(toProblem(failure, response.status)));
      return;
    }
    setRecovery(data.recoveryCodes);
  };

  if (recovery) {
    return (
      <div className="space-y-4">
        <Alert variant="success" title={t("enabledTitle")}>
          {t("recoveryBody")}
        </Alert>
        <ul className="grid grid-cols-2 gap-2 rounded-card border border-line bg-sunken p-4 font-mono text-sm sm:grid-cols-5" aria-label={t("recoveryCodes")}>
          {recovery.map((c) => (
            <li key={c} className="select-all text-center text-strong">
              {c}
            </li>
          ))}
        </ul>
        <div className="flex flex-wrap gap-3">
          <Button
            variant="secondary"
            onClick={async () => {
              await navigator.clipboard?.writeText(recovery.join("\n"));
              toast.success(t("copied"));
            }}
          >
            <Copy aria-hidden /> {t("copy")}
          </Button>
          <Button onClick={onEnabled}>{t("savedThem")}</Button>
        </div>
      </div>
    );
  }

  if (!setup) {
    return (
      <div className="space-y-4">
        {error ? <Alert variant="danger">{error}</Alert> : null}
        <p className="text-muted">{t("intro")}</p>
        <Button onClick={begin} loading={busy}>
          <ShieldCheck aria-hidden /> {t("start")}
        </Button>
      </div>
    );
  }

  return (
    <form onSubmit={enable} className="space-y-4" noValidate>
      {error ? <Alert variant="danger">{error}</Alert> : null}
      <ol className="list-decimal space-y-3 ps-5 text-strong">
        <li>{t("step1")}</li>
        <li>
          <div className="mt-3 flex flex-col items-start gap-3 sm:flex-row sm:items-center">
            {/* eslint-disable-next-line @next/next/no-img-element -- a data: URL QR code, nothing for next/image to optimise */}
            {qr ? <img src={qr} alt={t("qrAlt")} width={224} height={224} className="rounded-control border border-line bg-white p-1" /> : null}
            <div className="min-w-0 space-y-1">
              <p className="text-sm text-muted">{t("orKey")}</p>
              <code className="block select-all break-all rounded-control bg-sunken px-3 py-2 text-sm text-strong">{setup.sharedKey}</code>
            </div>
          </div>
        </li>
        <li>{t("step3")}</li>
      </ol>
      <Field label={t("code")} htmlFor="totp" required>
        <Input id="totp" inputMode="numeric" autoComplete="one-time-code" maxLength={8} value={code} onChange={(e) => setCode(e.target.value)} />
      </Field>
      <Button type="submit" loading={busy} disabled={code.trim().length < 6}>
        {t("enable")}
      </Button>
    </form>
  );
}
