"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, Printer } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { useSettlement } from "@/modules/couriers/api";
import { Difference, SettlementStatusBadge, useBranchLabel, useMoney2 } from "./common";
import { num } from "./logic";

/** `/admin/cod/[id]/print`: a clean sheet of the batch (items, totals, signature lines). The admin frame is hidden when printing. */
export function SettlementPrint({ id }: { id: string }) {
  const t = useTranslations("cod.print");
  const tn = useTranslations("nav");
  const money = useMoney2();
  const dateTime = useDateTime();
  const branchLabel = useBranchLabel();
  const query = useSettlement(id);
  const s = query.data;

  return (
    <>
      <div className="print:hidden">
        <PageHeader
          title={t("title")}
          description={s?.number}
          crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.codSettlements"), href: "/admin/cod" }, { label: s?.number ?? "...", href: `/admin/cod/${id}` }, { label: t("title") }]}
          actions={
            <>
              <Button asChild variant="secondary">
                <Link href={`/admin/cod/${id}`}>
                  <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
                </Link>
              </Button>
              <Button onClick={() => window.print()} disabled={!s} data-testid="print-now">
                <Printer aria-hidden /> {t("printNow")}
              </Button>
            </>
          }
        />
      </div>
      {query.isPending ? <Skeleton className="h-96" aria-busy="true" /> : null}
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      {s ? (
        <article className="rounded-card border border-line bg-card p-5 print:rounded-none print:border-0 print:p-0" data-testid="settlement-sheet" data-number={s.number}>
          <header className="flex flex-wrap items-start justify-between gap-3 border-b border-line pb-4">
            <div>
              <h2 className="text-xl font-bold text-strong">{t("sheetTitle")}</h2>
              <p className="font-mono text-lg font-semibold text-strong">{s.number}</p>
              <p className="mt-1 text-sm"><SettlementStatusBadge status={s.status} className="print:border print:border-line" /></p>
            </div>
            <dl className="text-sm sm:text-end">
              <div>
                <dt className="inline text-muted">{t("courier")}: </dt>
                <dd className="inline font-semibold text-strong">{s.courierName}</dd>
              </div>
              <div>
                <dt className="inline text-muted">{t("branch")}: </dt>
                <dd className="inline font-semibold text-strong">{branchLabel(s.branchId).label}</dd>
              </div>
              <div>
                <dt className="inline text-muted">{t("created")}: </dt>
                <dd className="inline text-strong">{dateTime(s.createdAt)}</dd>
              </div>
              {s.reference ? (
                <div>
                  <dt className="inline text-muted">{t("reference")}: </dt>
                  <dd className="inline font-mono font-semibold text-strong">{s.reference}</dd>
                </div>
              ) : null}
            </dl>
          </header>

          <table className="mt-4 w-full table-fixed border-collapse text-sm">
            <caption className="sr-only">{t("itemsCaption")}</caption>
            <thead>
              <tr className="border-b-2 border-strong text-start">
                <th scope="col" className="w-[34%] py-2 pe-2 text-start font-semibold">{t("columns.parcel")}</th>
                <th scope="col" className="w-[22%] py-2 pe-2 text-end font-semibold">{t("columns.expected")}</th>
                <th scope="col" className="w-[22%] py-2 pe-2 text-end font-semibold">{t("columns.received")}</th>
                <th scope="col" className="w-[22%] py-2 text-end font-semibold">{t("columns.difference")}</th>
              </tr>
            </thead>
            <tbody>
              {s.items.map((item) => (
                <tr key={item.shipmentId} className="border-b border-line align-top print:break-inside-avoid" data-testid="sheet-row">
                  <td className="py-2 pe-2 [overflow-wrap:anywhere]">
                    <span className="block font-mono font-semibold text-strong">{item.consignmentId}</span>
                    <span className="block font-mono text-muted">{item.orderNumber}</span>
                    {item.note ? <span className="block text-muted">{item.note}</span> : null}
                  </td>
                  <td className="py-2 pe-2 text-end tabular-nums">{money(item.expected)}</td>
                  <td className="py-2 pe-2 text-end tabular-nums">{item.received === null ? "" : money(item.received)}</td>
                  <td className="py-2 text-end tabular-nums">{item.difference === null ? "" : <Difference difference={item.difference} />}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr className="border-t-2 border-strong font-bold text-strong">
                <th scope="row" className="py-2 pe-2 text-start">{t("totals", { count: s.items.length })}</th>
                <td className="py-2 pe-2 text-end tabular-nums" data-testid="sheet-expected">{money(s.expectedTotal)}</td>
                <td className="py-2 pe-2 text-end tabular-nums" data-testid="sheet-received">{s.receivedTotal === null ? "" : money(s.receivedTotal)}</td>
                <td className="py-2 text-end tabular-nums" data-testid="sheet-difference">{s.difference === null ? "" : <Difference difference={s.difference} />}</td>
              </tr>
            </tfoot>
          </table>

          <p className="mt-3 text-sm text-muted">
            {t("summary", { short: num(s.shortParcels), over: num(s.overParcels) })} {t("feesNote", { fees: money(s.feesTotal) })}
          </p>

          <div className="mt-12 grid gap-10 sm:grid-cols-3 print:break-inside-avoid" data-testid="signatures">
            {(["preparedBy", "courierBy", "receivedBy"] as const).map((key) => (
              <div key={key}>
                <div className="h-10 border-b border-strong" />
                <p className="mt-1 text-sm font-medium text-strong">{t(key)}</p>
                <p className="text-sm text-muted">{t("signDate")}</p>
              </div>
            ))}
          </div>
        </article>
      ) : null}
    </>
  );
}
