"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Tabs } from "@/components/ui/tabs";
import { Alert } from "@/components/ui/surfaces";
import { StaffLoginForm } from "./staff-login-form";
import { CustomerOtpForm } from "./customer-otp-form";

/** One login page for both audiences: staff (email or user name + password) and customers (mobile + OTP). */
export function LoginCard({ next, reason, initial = "staff" }: { next?: string; reason?: string; initial?: "staff" | "customer" }) {
  const t = useTranslations("auth");
  const [tab, setTab] = React.useState<string>(initial);

  const notice =
    reason === "expired" ? t("reasonExpired") : reason === "signed-out" ? t("reasonSignedOut") : reason === "two-factor-enabled" ? t("reasonTwoFactor") : reason === "password-changed" ? t("reasonPasswordChanged") : null;

  return (
    <div className="space-y-5">
      <div className="space-y-1.5 text-center">
        <h1 className="text-3xl font-semibold">{t("title")}</h1>
        <p className="text-muted">{t("subtitle")}</p>
      </div>
      {notice ? <Alert variant={reason === "expired" ? "warning" : "success"}>{notice}</Alert> : null}
      <Tabs
        label={t("title")}
        value={tab}
        onValueChange={setTab}
        tabs={[
          { id: "staff", label: t("staffTab") },
          { id: "customer", label: t("customerTab") },
        ]}
      >
        {(active) => (active === "staff" ? <StaffLoginForm next={next} /> : <CustomerOtpForm next={next} />)}
      </Tabs>
    </div>
  );
}
