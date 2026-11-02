import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { ErrorScreen } from "@/components/layout/error-screen";

export const metadata: Metadata = { title: "401", robots: { index: false } };

/** Shown when a session has ended and the person needs to sign in again. */
export default async function UnauthorizedPage() {
  const t = await getTranslations("errorPages");
  return <ErrorScreen code="401" title={t("unauthorizedTitle")} body={t("unauthorizedBody")} primary={{ label: t("signIn"), href: "/login" }} secondary={{ label: t("goHome"), href: "/" }} />;
}
