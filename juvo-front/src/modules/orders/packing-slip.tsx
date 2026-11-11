"use client";

import * as React from "react";
import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { Printer } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { api, unwrap } from "@/lib/api/client";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { usePackingSlip, useOrderErrorText, type PackingSlip } from "./api";
import { AmountList, useMethodLabel } from "./common";
import { AddressBlock } from "./my-order-detail";

/** One packing slip: what the packer needs (items, where it goes, how much to collect). Printed as it looks on the screen. */
export function PackingSlipSheet({ slip, last = true }: { slip: PackingSlip; last?: boolean }) {
  const t = useTranslations("orders.slip");
  const money = useMoney();
  const dateTime = useDateTime();
  const methodLabel = useMethodLabel();
  const collect = toNumber(slip.collectOnDelivery);
  return (
    <article className={`rounded-card border border-line bg-card p-5 print:rounded-none print:border-0 print:p-0 ${last ? "" : "print:break-after-page"} print:break-inside-avoid-page`} data-testid="packing-slip" data-number={slip.number}>
      <header className="flex flex-wrap items-start justify-between gap-3 border-b border-line pb-4">
        <div>
          <h2 className="text-xl font-bold text-strong">{t("title")}</h2>
          <p className="font-mono text-lg font-semibold text-strong" data-testid="slip-number">
            {slip.number}
          </p>
          <p className="text-sm text-muted">{dateTime(slip.placedAt)}</p>
        </div>
        <div className="text-end text-sm text-muted">
          <p className="font-semibold text-strong">{t("from")}</p>
          <p>{slip.branchName}</p>
          {slip.branchPickupAddress ? <p className="[overflow-wrap:anywhere]">{slip.branchPickupAddress}</p> : null}
          {slip.branchPhone ? <p>{slip.branchPhone}</p> : null}
        </div>
      </header>

      <section className="border-b border-line py-4" aria-label={t("to")}>
        <h3 className="mb-1 text-sm font-semibold text-muted">{t("to")}</h3>
        <AddressBlock shipping={slip.shipping} />
        {slip.customerNote ? (
          <p className="mt-3 rounded-control bg-sunken p-3 text-sm text-strong print:border print:border-line [overflow-wrap:anywhere]">
            <span className="font-semibold">{t("note")}: </span>
            {slip.customerNote}
          </p>
        ) : null}
      </section>

      <section className="border-b border-line py-4" aria-label={t("items")}>
        <h3 className="mb-2 text-sm font-semibold text-muted">{t("items")}</h3>
        <ul className="divide-y divide-line">
          {slip.lines.map((line) => (
            <li key={line.sku} className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-2" data-sku={line.sku}>
              <div className="min-w-0 flex-1 basis-48">
                <p className="font-medium text-strong [overflow-wrap:anywhere]">{line.name}</p>
                <p className="text-sm text-muted [overflow-wrap:anywhere]">{[line.sku, line.colorName, line.size].filter(Boolean).join(" · ")}</p>
              </div>
              <p className="text-end text-sm text-muted">
                <span className="text-lg font-bold text-strong">× {toNumber(line.quantity)}</span> · {money(line.unitPrice)} · <span className="font-semibold text-strong">{money(line.lineTotal)}</span>
              </p>
            </li>
          ))}
        </ul>
        <p className="mt-2 text-sm text-muted">
          {t("units", { count: toNumber(slip.totalQuantity) })} · {t("weight", { grams: toNumber(slip.weightGrams) })}
        </p>
      </section>

      <section className="pt-4" aria-label={t("amounts")}>
        <AmountList
          rows={[
            { key: "subtotal", label: t("subtotal"), value: slip.subtotal, always: true },
            { key: "discount", label: t("discount"), value: slip.discount, negative: true },
            { key: "delivery", label: t("delivery"), value: slip.deliveryCharge, always: true },
            { key: "total", label: t("total"), value: slip.grandTotal, strong: true },
          ]}
        />
        <p className="mt-3 text-sm text-muted">{t("payment", { method: methodLabel(slip.paymentMethod) })}</p>
        <p className="mt-2 rounded-control border-2 border-strong p-3 text-center text-lg font-bold text-strong" data-testid="collect">
          {collect > 0 ? t("collect", { amount: money(collect) }) : t("nothingToCollect")}
        </p>
      </section>
    </article>
  );
}

function PrintButton() {
  const t = useTranslations("orders.slip");
  return (
    <Button onClick={() => window.print()} data-testid="print-now" className="print:hidden">
      <Printer aria-hidden /> {t("print")}
    </Button>
  );
}

/** `/admin/orders/[id]/packing-slip`: one slip with a print button. */
export function PackingSlipView({ id }: { id: string }) {
  const t = useTranslations("orders.slip");
  const tn = useTranslations("nav");
  const errorText = useOrderErrorText();
  const query = usePackingSlip(id);
  return (
    <>
      <div className="print:hidden">
        <PageHeader
          title={t("title")}
          description={query.data?.number}
          crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.orders"), href: "/admin/orders" }, { label: query.data?.number ?? "...", href: `/admin/orders/${id}` }, { label: t("title") }]}
          actions={<PrintButton />}
        />
      </div>
      {query.isLoading ? <Skeleton className="h-96" aria-busy /> : null}
      {query.isError ? <Alert variant="danger">{errorText(query.error)}</Alert> : null}
      {query.data ? <PackingSlipSheet slip={query.data} /> : null}
    </>
  );
}

/** `/admin/orders/print?ids=a,b,c`: the slips of several orders (bulk "print"), one per page when printed. */
export function BulkSlipsView({ ids }: { ids: string[] }) {
  const t = useTranslations("orders.slip");
  const tn = useTranslations("nav");
  const errorText = useOrderErrorText();
  const query = useQuery({
    queryKey: ["orders", "slips", ids],
    enabled: ids.length > 0,
    queryFn: async () => unwrap(await api.POST("/api/v1/orders/bulk", { body: { action: "print", ids, reason: null, advanceReceived: false } })),
  });
  const slips = query.data?.slips ?? [];
  const failed = (query.data?.results ?? []).filter((r) => !r.ok);
  return (
    <>
      <div className="print:hidden">
        <PageHeader
          title={t("bulkTitle")}
          description={t("bulkCount", { count: slips.length })}
          crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.orders"), href: "/admin/orders" }, { label: t("bulkTitle") }]}
          actions={slips.length > 0 ? <PrintButton /> : undefined}
        />
        {failed.length > 0 ? (
          <Alert variant="warning" className="mb-4" title={t("bulkFailed", { count: failed.length })}>
            {failed.map((f) => f.number ?? f.orderId).join(", ")}
          </Alert>
        ) : null}
      </div>
      {ids.length === 0 ? (
        <Alert variant="warning">
          {t("bulkNone")}{" "}
          <Link href="/admin/orders" className="inline-flex min-h-11 items-center font-semibold text-primary underline-offset-4 hover:underline">
            {t("backToOrders")}
          </Link>
        </Alert>
      ) : null}
      {query.isLoading ? <Skeleton className="h-96" aria-busy /> : null}
      {query.isError ? <Alert variant="danger">{errorText(query.error)}</Alert> : null}
      <div className="space-y-6 print:space-y-0">
        {slips.map((slip, index) => (
          <PackingSlipSheet key={slip.number} slip={slip} last={index === slips.length - 1} />
        ))}
      </div>
    </>
  );
}
