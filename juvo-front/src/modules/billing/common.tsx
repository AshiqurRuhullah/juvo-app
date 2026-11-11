"use client";

import { useTranslations } from "next-intl";
import { Download } from "lucide-react";
import { Button } from "@/components/ui/button";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";

/** Shows the friendly text of an API problem as a toast and returns the field errors, if any, for the form. */
export function useReportBillingError() {
  const message = useProblemMessage();
  return (error: unknown): Record<string, string> => {
    const problem = error instanceof ApiError ? error.problem : null;
    toast.error(message(problem));
    return problem ? fieldErrors(problem) : {};
  };
}

export const isNotFound = (error: unknown) => error instanceof ApiError && error.problem.status === 404;

/** Two plain download links (English and Bengali PDF). They are real links through the BFF, so the browser sends the cookies. */
export function PdfLinks({ href, testId }: { href: (lang: "en" | "bn") => string; testId: string }) {
  const t = useTranslations("billing.common");
  return (
    <>
      {(["en", "bn"] as const).map((lang) => (
        <Button key={lang} asChild variant="secondary">
          <a href={href(lang)} download data-testid={`${testId}-${lang}`}>
            <Download aria-hidden /> {lang === "en" ? t("pdfEn") : t("pdfBn")}
          </a>
        </Button>
      ))}
    </>
  );
}
