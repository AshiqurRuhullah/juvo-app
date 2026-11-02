import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { ErrorScreen } from "@/components/layout/error-screen";

export const metadata: Metadata = { title: "403", robots: { index: false } };

/** Signed in, but not allowed here (no permission in the active branch, or a customer on an admin page). */
export default async function ForbiddenPage() {
  const t = await getTranslations("errorPages");
  return <ErrorScreen code="403" title={t("forbiddenTitle")} body={t("forbiddenBody")} primary={{ label: t("goBack"), href: "/admin" }} secondary={{ label: t("goHome"), href: "/" }} />;
}
