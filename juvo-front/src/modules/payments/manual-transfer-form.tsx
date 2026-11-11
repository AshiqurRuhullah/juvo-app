"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Clock, Landmark, Upload } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Alert, Card } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useMoney } from "@/modules/catalog/format";
import { useManualPayment, usePaymentMethods, useUploadProof, type PaymentSession } from "./api";
import { usePaymentErrorText } from "./common";
import { proofProblem } from "./status";

/**
 * Manual bank or mobile transfer: the account details the owner wrote, then the reference (and payer name, and a screenshot if the
 * customer has one). Sending it puts the payment under review; staff approve or reject. After sending, the screenshot can still be
 * added.
 */
export function ManualTransferForm({ number, token, amount, reviewing, trackHref }: { number: string; token: string | null; amount: number; reviewing: boolean; trackHref: string }) {
  const t = useTranslations("payments.manual");
  const money = useMoney();
  const errorText = usePaymentErrorText();
  const methods = usePaymentMethods(amount);
  const manual = useManualPayment();
  const upload = useUploadProof();
  const instructions = methods.data?.find((m) => m.code === "MANUAL")?.instructions ?? null;

  const [reference, setReference] = React.useState("");
  const [payer, setPayer] = React.useState("");
  const [file, setFile] = React.useState<File | null>(null);
  const [errors, setErrors] = React.useState<Record<string, string>>({});
  const [formError, setFormError] = React.useState<string | null>(null);
  const [sent, setSent] = React.useState<PaymentSession | null>(null);
  const [proofSent, setProofSent] = React.useState(false);
  const [proofError, setProofError] = React.useState<string | null>(null);

  const problemText = (kind: "type" | "size") => (kind === "type" ? t("proofType") : t("proofSize"));

  const sendProof = async (paymentId: string, chosen: File) => {
    setProofError(null);
    try {
      await upload.mutateAsync({ paymentId, token, file: chosen });
      setProofSent(true);
      toast.success(t("proofSaved"));
    } catch (failure) {
      setProofError(errorText(failure));
    }
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    if (!reference.trim()) next.reference = t("errors.reference");
    if (!payer.trim()) next.payer = t("errors.payer");
    if (file) {
      const problem = proofProblem(file);
      if (problem) next.file = problemText(problem);
    }
    setErrors(next);
    setFormError(null);
    if (Object.keys(next).length) return;
    try {
      const session = await manual.mutateAsync({ orderNumber: number, token, reference: reference.trim(), payerName: payer.trim() });
      setSent(session);
      if (file) await sendProof(session.paymentId, file);
    } catch (failure) {
      const code = failure instanceof ApiError ? failure.problem.code : undefined;
      if (code === "PAYMENT.REFERENCE_REUSED" || code === "PAYMENT.REFERENCE_REQUIRED") setErrors({ reference: errorText(failure) });
      else setFormError(errorText(failure));
    }
  };

  if (sent || reviewing) {
    return (
      <Card className="space-y-4 p-4 sm:p-5" data-testid="manual-review">
        <Alert variant="info" title={t("reviewTitle")}>
          <Clock className="me-1 inline size-4" aria-hidden /> {t("reviewText")}
        </Alert>
        {sent && !file && !proofSent ? (
          <div className="space-y-3 border-t border-line pt-4">
            <p className="text-sm text-muted">{t("proofLater")}</p>
            <Field label={t("proof")} htmlFor="manual-proof-late" hint={t("proofHint")} error={proofError ?? undefined}>
              <Input
                id="manual-proof-late"
                type="file"
                accept={["image/jpeg", "image/png", "image/webp", "application/pdf"].join(",")}
                onChange={(e) => {
                  const chosen = e.target.files?.[0];
                  if (!chosen) return;
                  const problem = proofProblem(chosen);
                  if (problem) setProofError(problemText(problem));
                  else void sendProof(sent.paymentId, chosen);
                }}
              />
            </Field>
          </div>
        ) : null}
        {proofSent ? <Alert variant="success">{t("proofSaved")}</Alert> : null}
        {proofError && (file || proofSent) ? <Alert variant="warning">{proofError}</Alert> : null}
        <Link href={trackHref} className={buttonVariants({ size: "lg", className: "w-full" })}>
          {t("viewOrder")}
        </Link>
      </Card>
    );
  }

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="space-y-5" aria-label={t("title")}>
      <Card className="space-y-3 p-4 sm:p-5">
        <h2 className="flex items-center gap-2 text-lg font-semibold text-strong">
          <Landmark className="size-5 text-primary" aria-hidden /> {t("title")}
        </h2>
        <p className="text-muted">{t("help", { amount: money(amount) })}</p>
        {instructions ? (
          <p className="whitespace-pre-line rounded-control border border-line bg-sunken p-3 text-strong [overflow-wrap:anywhere]" data-testid="manual-instructions">
            {instructions}
          </p>
        ) : (
          <Alert variant="warning">{t("noInstructions")}</Alert>
        )}
      </Card>
      <Card className="space-y-4 p-4 sm:p-5">
        <Field label={t("reference")} htmlFor="manual-reference" required hint={t("referenceHint")} error={errors.reference}>
          <Input id="manual-reference" value={reference} onChange={(e) => setReference(e.target.value)} maxLength={120} autoComplete="off" aria-invalid={!!errors.reference} />
        </Field>
        <Field label={t("payer")} htmlFor="manual-payer" required hint={t("payerHint")} error={errors.payer}>
          <Input id="manual-payer" value={payer} onChange={(e) => setPayer(e.target.value)} maxLength={120} autoComplete="name" aria-invalid={!!errors.payer} />
        </Field>
        <Field label={t("proof")} htmlFor="manual-proof" hint={t("proofHint")} error={errors.file}>
          <Input id="manual-proof" type="file" accept={["image/jpeg", "image/png", "image/webp", "application/pdf"].join(",")} onChange={(e) => setFile(e.target.files?.[0] ?? null)} aria-invalid={!!errors.file} />
        </Field>
        {formError ? <Alert variant="danger" data-testid="manual-error">{formError}</Alert> : null}
        <Button type="submit" size="lg" className="w-full" loading={manual.isPending || upload.isPending} disabled={!instructions} data-testid="manual-submit">
          <Upload aria-hidden /> {t("submit")}
        </Button>
      </Card>
    </form>
  );
}
