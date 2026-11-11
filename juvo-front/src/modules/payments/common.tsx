"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Banknote, Landmark, FlaskConical, CreditCard } from "lucide-react";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { ApiError } from "@/lib/api/client";
import { toast } from "@/components/ui/toaster";

/** The shop's own word for a payment method code (COD, SANDBOX, MANUAL ...), or the name the API gave. */
export function usePayMethodLabel() {
  const t = useTranslations("payments.method");
  return (code: string, fallback?: string | null) => (t.has(code) ? t(code) : (fallback ?? code));
}

export function MethodIcon({ code, className }: { code: string; className?: string }) {
  if (code === "COD") return <Banknote className={className} aria-hidden />;
  if (code === "MANUAL") return <Landmark className={className} aria-hidden />;
  if (code === "SANDBOX") return <FlaskConical className={className} aria-hidden />;
  return <CreditCard className={className} aria-hidden />;
}

/** Friendly text for an API problem as a string (errors.PAYMENT_*, errors.INVOICE_* ...). */
export function usePaymentErrorText() {
  const message = useProblemMessage();
  return (error: unknown): string => message(error instanceof ApiError ? error.problem : null);
}

export function useReportPaymentError() {
  const text = usePaymentErrorText();
  return (error: unknown) => toast.error(text(error));
}

export const problemCode = (error: unknown) => (error instanceof ApiError ? error.problem.code : undefined);

/** Reads the browser's session storage safely (null on the server or when blocked). */
export function useSessionStorage(): Storage | null {
  return React.useSyncExternalStore(
    () => () => undefined,
    () => {
      try {
        return window.sessionStorage;
      } catch {
        return null;
      }
    },
    () => null,
  );
}

/** Seconds since the page opened, ticking once a second (for the hold timer). Starts at the real time on the client. */
export function useNow(): number {
  return React.useSyncExternalStore(
    (notify) => {
      const id = window.setInterval(notify, 1000);
      return () => window.clearInterval(id);
    },
    () => Math.floor(Date.now() / 1000) * 1000,
    () => 0,
  );
}
