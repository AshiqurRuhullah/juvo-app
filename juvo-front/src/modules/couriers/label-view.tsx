"use client";

import * as React from "react";
import Link from "next/link";
import { useQueries } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { Printer } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { api, unwrap } from "@/lib/api/client";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { COURIER_KEYS, type ShipmentLabel } from "./api";
import { Barcode } from "./barcode";
import { formatWeight } from "./status";
import "./label.css";

/** One printable label: barcode, who it goes to, who sends it and the cash to collect. Black on white, A6 when printed. */
export function LabelSheet({ label }: { label: ShipmentLabel }) {
  const t = useTranslations("shipments.label");
  const money = useMoney();
  const dateTime = useDateTime();
  const cod = toNumber(label.codAmount);
  return (
    <article className="label-paper flex w-full max-w-[105mm] flex-col gap-2 rounded-card border border-line p-4 text-sm shadow-card" data-testid="label" data-consignment={label.consignmentId}>
      <header className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-base font-bold [overflow-wrap:anywhere]">{label.courierName}</p>
          <p className="text-xs text-muted">{dateTime(label.bookedAt)}</p>
        </div>
        {label.isReturn ? <p className="shrink-0 border-2 border-strong px-2 py-0.5 text-sm font-bold uppercase tracking-wide">{t("returnParcel")}</p> : null}
      </header>

      <div className="border-y-2 border-strong py-2">
        <Barcode value={label.barcode} height={64} />
        <p className="mt-1 text-center font-mono text-base font-bold tracking-wider [overflow-wrap:anywhere]" data-testid="label-consignment">
          {label.consignmentId}
        </p>
      </div>

      <p className="flex items-baseline justify-between gap-2">
        <span className="text-muted">{t("order")}</span>
        <span className="font-mono font-bold" data-testid="label-order">{label.orderNumber}</span>
      </p>

      <section aria-label={t("to")} className="border-2 border-strong p-2">
        <h2 className="text-xs font-bold uppercase tracking-wide text-muted">{t("to")}</h2>
        <p className="text-lg font-bold leading-tight [overflow-wrap:anywhere]">{label.deliveryName}</p>
        <p className="font-semibold">{label.deliveryPhone}</p>
        <p className="[overflow-wrap:anywhere]">{label.deliveryAddress}</p>
        {label.deliveryDistrict ? <p className="font-semibold uppercase">{label.deliveryDistrict}</p> : null}
      </section>

      <section aria-label={t("from")} className="px-1">
        <h2 className="text-xs font-bold uppercase tracking-wide text-muted">{t("from")}</h2>
        <p className="font-semibold [overflow-wrap:anywhere]">{label.pickupName}</p>
        {label.pickupPhone ? <p>{label.pickupPhone}</p> : null}
        <p className="text-xs [overflow-wrap:anywhere]">{label.pickupAddress}</p>
      </section>

      <p
        className="mt-auto border-2 border-strong p-2 text-center"
        data-testid="label-cod"
        data-due={cod > 0}
      >
        {cod > 0 ? (
          <>
            <span className="block text-xs font-bold uppercase tracking-wide">{t("collect")}</span>
            <span className="block text-3xl font-black tabular-nums text-danger" data-testid="label-cod-amount">{money(cod)}</span>
          </>
        ) : (
          <span className="block text-base font-bold uppercase">{t("prepaid")}</span>
        )}
      </p>

      <footer className="flex flex-wrap items-baseline justify-between gap-x-3 text-xs">
        <span>{t("items", { count: toNumber(label.items) })}</span>
        <span>{formatWeight(toNumber(label.weightGrams))}</span>
      </footer>
      {label.note ? <p className="text-xs [overflow-wrap:anywhere]"><span className="font-bold">{t("note")}: </span>{label.note}</p> : null}
    </article>
  );
}

/** `/admin/shipments/[id]/label` (one) and `/admin/shipments/labels?ids=` (several, one per page). All labels load in parallel. */
export function LabelView({ ids, single = false }: { ids: string[]; single?: boolean }) {
  const t = useTranslations("shipments.label");
  const tn = useTranslations("nav");
  const results = useQueries({
    queries: ids.map((id) => ({
      queryKey: [...COURIER_KEYS.shipments, "label", id],
      queryFn: async () => unwrap(await api.GET("/api/v1/shipments/{id}/label", { params: { path: { id } } })),
    })),
  });
  const loaded = results.flatMap((r) => (r.data ? [r.data] : []));
  const pending = results.some((r) => r.isPending);
  const failed = results.filter((r) => r.isError).length;

  return (
    <>
      <div className="print:hidden">
        <PageHeader
          title={single ? t("title") : t("bulkTitle")}
          description={single ? loaded[0]?.consignmentId : t("bulkCount", { count: loaded.length })}
          crumbs={[
            { label: tn("items.dashboard"), href: "/admin" },
            { label: tn("items.shipments"), href: "/admin/shipments" },
            ...(single && ids[0] ? [{ label: loaded[0]?.consignmentId ?? "...", href: `/admin/shipments/${ids[0]}` }] : []),
            { label: single ? t("title") : t("bulkTitle") },
          ]}
          actions={
            loaded.length > 0 ? (
              <Button onClick={() => window.print()} data-testid="print-now">
                <Printer aria-hidden /> {t("print")}
              </Button>
            ) : undefined
          }
        />
        {failed > 0 ? (
          <Alert variant="warning" className="mb-4" title={t("failed", { count: failed })}>
            {t("failedHint")}
          </Alert>
        ) : null}
      </div>

      {ids.length === 0 ? (
        <EmptyState title={t("noneSelected")} description={t("noneSelectedHint")} action={<Button asChild variant="secondary"><Link href="/admin/shipments">{t("backToList")}</Link></Button>} />
      ) : pending && loaded.length === 0 ? (
        <Skeleton className="h-[148mm] max-w-[105mm]" aria-busy="true" />
      ) : (
        <div className="label-print-root flex flex-wrap items-start gap-4" data-testid="labels">
          {loaded.map((label) => (
            <LabelSheet key={label.shipmentId} label={label} />
          ))}
        </div>
      )}
    </>
  );
}
