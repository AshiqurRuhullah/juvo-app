"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Alert } from "@/components/ui/surfaces";
import { postJson } from "@/lib/api/post";
import { ChangePasswordForm } from "./password-forms";
import { TwoFactorSetup } from "./two-factor";
import { hardNavigate } from "@/lib/navigation";

/**
 * A limited session (new account, admin reset, or Super Admin without two-step verification) can only do two things: choose a
 * permanent password and set up two-step verification. Everything else answers 403 until these are done.
 */
export function FirstLoginFlow({ mustChangePassword, requiresTwoFactorSetup }: { mustChangePassword: boolean; requiresTwoFactorSetup: boolean }) {
  const t = useTranslations("firstLogin");
  const [step, setStep] = React.useState<"password" | "twoFactor">(mustChangePassword ? "password" : "twoFactor");

  if (step === "password") {
    return (
      <div className="space-y-5">
        <div className="space-y-1.5">
          <h1 className="text-2xl font-semibold">{t("passwordTitle")}</h1>
          <p className="text-muted">{t("passwordBody")}</p>
        </div>
        <ChangePasswordForm
          requireCurrent={false}
          onChanged={({ requiresTwoFactorSetup: needs }) => {
            if (needs || requiresTwoFactorSetup) setStep("twoFactor");
            else hardNavigate("/admin");
          }}
        />
      </div>
    );
  }

  return (
    <div className="space-y-5">
      <div className="space-y-1.5">
        <h1 className="text-2xl font-semibold">{t("twoFactorTitle")}</h1>
        <p className="text-muted">{t("twoFactorBody")}</p>
      </div>
      <Alert variant="info">{t("twoFactorNotice")}</Alert>
      <TwoFactorSetup
        onEnabled={async () => {
          // The session flags change when two-step verification is on, so sign in again with the new code.
          await postJson("/api/bff/auth/logout");
          hardNavigate("/login?reason=two-factor-enabled");
        }}
      />
    </div>
  );
}
