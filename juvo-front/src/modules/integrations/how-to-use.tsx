"use client";

import { useTranslations } from "next-intl";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { CodeBlock } from "./common";
import { API_KEY_PLACEHOLDER, HOST_PLACEHOLDER, PUBLIC_API_PATH, PUBLIC_OPENAPI_PATH, curlExample } from "./logic";

/**
 * "How to use" for API keys. This site does not know the API's public address (the browser only talks to this site), so the address is
 * written with a placeholder and the path; the person fills in their API host. Nothing here is a real key.
 */
export function HowToUse() {
  const t = useTranslations("integrations.usage");
  return (
    <Card data-testid="how-to-use">
      <CardHeader>
        <CardTitle className="text-lg">{t("title")}</CardTitle>
        <CardDescription>{t("intro", { placeholder: API_KEY_PLACEHOLDER })}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <dl className="grid gap-3 text-sm sm:grid-cols-[auto_1fr] sm:gap-x-6">
          <dt className="font-medium text-strong">{t("baseUrl")}</dt>
          <dd className="font-mono [overflow-wrap:anywhere]" data-testid="usage-base">
            https://{HOST_PLACEHOLDER}
            {PUBLIC_API_PATH}
          </dd>
          <dt className="font-medium text-strong">{t("spec")}</dt>
          <dd className="font-mono [overflow-wrap:anywhere]" data-testid="usage-spec">
            https://{HOST_PLACEHOLDER}
            {PUBLIC_OPENAPI_PATH}
          </dd>
        </dl>
        <p className="text-sm text-muted">{t("hostNote", { host: HOST_PLACEHOLDER })}</p>
        <CodeBlock code={curlExample()} label={t("curlLabel")} testId="usage-curl" />
        <p className="text-sm text-muted">{t("limits")}</p>
      </CardContent>
    </Card>
  );
}
