"use client";

import { useEffect } from "react";
import { useTranslations } from "next-intl";
import { ErrorScreen } from "@/components/layout/error-screen";

/** Any unexpected failure while rendering a page. Shows the reference number so support can find the log entry. */
export default function ErrorPage({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  const t = useTranslations("errorPages");
  useEffect(() => {
    console.error(error);
  }, [error]);

  return <ErrorScreen code="500" title={t("errorTitle")} body={t("errorBody")} primary={{ label: t("tryAgain"), onClick: reset }} secondary={{ label: t("goHome"), href: "/" }} reference={error.digest} />;
}
