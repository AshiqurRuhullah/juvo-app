"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { CheckCircle2, Clock } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime } from "@/components/data/list-kit";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useApplyCost, useReceipt } from "./api";
import { useMoney2 } from "./common";
import { receiptCostPending } from "./logic";
import { usePurchasingPermissions } from "./permissions";

/** One goods receipt with its lines. A receipt booked by someone who may not change costs shows "cost not applied" and a button for people who may. */
export function ReceiptDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const t = useTranslations("purchasing.receipts.detail");
  const tc = useTranslations("purchasing.common");
  const money = useMoney2();
  const dateTime = useDateTime();
  const report = useReportError();
  const query = useReceipt(id);
  const apply = useApplyCost();
  const perms = usePurchasingPermissions(query.data?.branchId);
  const receipt = query.data;
  const pending = receipt ? receiptCostPending(receipt.lines) : false;

  const applyCost = async () => {
    try {
      await apply.mutateAsync(id);
      toast.success(t("applied"));
    } catch (e) {
      report(e);
    }
  };

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{receipt ? t("title", { number: receipt.number }) : t("loading")}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{receipt ? t("description", { order: receipt.purchaseOrderNumber, at: dateTime(receipt.receivedAt), by: receipt.receivedByName ?? "-" }) : t("loading")}</DialogDescription>
        {query.isLoading ? <Skeleton className="mt-4 h-32" aria-busy="true" /> : null}
        {query.isError ? <Alert variant="danger" className="mt-4">{tc("loadFailed")}</Alert> : null}
        {receipt ? (
          <div className="mt-4 space-y-4">
            {receipt.note ? <p className="text-sm text-muted [overflow-wrap:anywhere]">{receipt.note}</p> : null}
            <ul className="divide-y divide-line rounded-control border border-line" aria-label={t("lines")} data-testid="receipt-lines">
              {receipt.lines.map((line) => (
                <li key={line.id} className="flex flex-col gap-1 p-3 sm:flex-row sm:items-center sm:justify-between">
                  <span className="font-mono text-sm text-strong [overflow-wrap:anywhere]">{line.sku}</span>
                  <span className="text-sm tabular-nums text-muted">
                    {t("quantities", { accepted: toNumber(line.acceptedQuantity), rejected: toNumber(line.rejectedQuantity) })} · {money(line.unitCost)}
                  </span>
                  <span className="inline-flex items-center gap-1 text-sm" data-testid="cost-state">
                    {line.costApplied || toNumber(line.acceptedQuantity) === 0 ? (
                      <>
                        <CheckCircle2 className="size-4 text-success-ink" aria-hidden /> {t("costApplied")}
                      </>
                    ) : (
                      <>
                        <Clock className="size-4 text-warning-ink" aria-hidden /> {t("costPending")}
                      </>
                    )}
                  </span>
                </li>
              ))}
            </ul>
            {pending ? (
              perms.viewCost ? (
                <Button onClick={() => void applyCost()} loading={apply.isPending} data-testid="apply-cost">
                  {t("apply")}
                </Button>
              ) : (
                <Alert variant="info">{t("needCostPermission")}</Alert>
              )
            ) : null}
          </div>
        ) : null}
        <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          {receipt && perms.poView ? (
            <Button asChild variant="secondary">
              <Link href={`/admin/purchasing/orders/${receipt.purchaseOrderId}`}>{t("openOrder")}</Link>
            </Button>
          ) : null}
          <DialogClose asChild>
            <Button variant="secondary">{tc("close")}</Button>
          </DialogClose>
        </div>
      </ModalContent>
    </Dialog>
  );
}
