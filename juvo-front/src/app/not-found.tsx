import { getTranslations } from "next-intl/server";
import { ErrorScreen } from "@/components/layout/error-screen";

export default async function NotFound() {
  const t = await getTranslations("errorPages");
  return <ErrorScreen code="404" title={t("notFoundTitle")} body={t("notFoundBody")} primary={{ label: t("goHome"), href: "/" }} />;
}
