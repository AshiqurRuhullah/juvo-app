"use client";

import { useTranslations } from "next-intl";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { CodeBlock } from "./common";
import { TOLERANCE_SECONDS, verifySample } from "./logic";

/** How a receiver checks `Juvo-Signature`. The sample uses a placeholder, never a real secret. */
export function VerifyHelp() {
  const t = useTranslations("integrations.verify");
  return (
    <Card data-testid="verify-help">
      <CardHeader>
        <CardTitle className="text-lg">{t("title")}</CardTitle>
        <CardDescription>{t("intro")}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <ol className="list-decimal space-y-1 ps-5 text-sm">
          <li>{t("step1")}</li>
          <li>{t("step2")}</li>
          <li>{t("step3", { seconds: TOLERANCE_SECONDS })}</li>
          <li>{t("step4")}</li>
        </ol>
        <CodeBlock code={verifySample()} label={t("sampleLabel")} testId="verify-sample" />
        <p className="text-sm text-muted">{t("answer")}</p>
      </CardContent>
    </Card>
  );
}
