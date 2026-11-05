"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Check, Copy } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert } from "@/components/ui/surfaces";

/** A one-time secret (a temporary password): shown once, with a copy button and a clear warning that it cannot be seen again. */
export function SecretReveal({ label, value }: { label: string; value: string }) {
  const t = useTranslations("ums.secret");
  const [copied, setCopied] = React.useState(false);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2500);
    } catch {
      setCopied(false); // the value is still on screen to copy by hand
    }
  };

  return (
    <div className="space-y-3">
      <Alert variant="warning" title={t("onceTitle")}>
        {t("onceBody")}
      </Alert>
      <div>
        <p className="mb-1.5 text-sm font-semibold text-muted">{label}</p>
        <div className="flex items-stretch gap-2">
          <code className="flex min-h-12 min-w-0 flex-1 items-center rounded-control border border-line bg-sunken px-4 font-mono text-base text-strong [overflow-wrap:anywhere]" data-testid="secret-value">
            {value}
          </code>
          <Button variant="secondary" onClick={copy} aria-label={t("copy")}>
            {copied ? <Check aria-hidden /> : <Copy aria-hidden />}
            <span className="hidden sm:inline">{copied ? t("copied") : t("copy")}</span>
          </Button>
        </div>
      </div>
    </div>
  );
}
