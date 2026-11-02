import type { Metadata } from "next";
import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { ResetPasswordForm } from "@/components/auth/password-forms";
import { Alert } from "@/components/ui/surfaces";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("auth");
  return { title: t("resetTitle"), robots: { index: false } };
}

export default async function ResetPasswordPage({ searchParams }: { searchParams: Promise<{ email?: string; token?: string }> }) {
  const { email, token } = await searchParams;
  const t = await getTranslations("auth");

  return (
    <div className="space-y-5">
      <h1 className="text-3xl font-semibold">{t("resetTitle")}</h1>
      {email && token ? (
        <ResetPasswordForm email={email} token={token} />
      ) : (
        <>
          <Alert variant="warning">{t("resetLinkInvalid")}</Alert>
          <Link href="/forgot-password" className="inline-flex min-h-11 items-center font-semibold text-primary hover:underline">
            {t("forgotTitle")}
          </Link>
        </>
      )}
    </div>
  );
}
