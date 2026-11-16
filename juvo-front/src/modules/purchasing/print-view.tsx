"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, Printer } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { usePurchasePrint, type PurchasePrint } from "./api";
import { PoStatusBadge, useDateOnly, useMoney2 } from "./common";

type Party = PurchasePrint["buyer"];

function PartyBlock({ title, party }: { title: string; party: Party }) {
  return (
    <div className="min-w-0 [overflow-wrap:anywhere]">
      <h3 className="text-sm font-semibold uppercase tracking-wide text-muted">{title}</h3>
      <p className="font-semibold text-strong">{party.name}</p>
      {party.contact ? <p className="text-sm text-strong">{party.contact}</p> : null}
      {party.phone ? <p className="text-sm text-strong">{party.phone}</p> : null}
      {party.email ? <p className="text-sm text-strong">{party.email}</p> : null}
      {party.address ? <p className="text-sm text-muted">{party.address}</p> : null}
    </div>
  );
}

/** `/admin/purchasing/orders/[id]/print`: a clean purchase order sheet from the API's print data. The admin frame is hidden when printing. */
export function PurchasePrintView({ id }: { id: string }) {
  const t = useTranslations("purchasing.print");
  const tn = useTranslations("nav");
  const money = useMoney2();
  const dateOnly = useDateOnly();
  const dateTime = useDateTime();
  const query = usePurchasePrint(id);
  const p = query.data;

  return (
    <>
      <div className="print:hidden">
        <PageHeader
          title={t("title")}
          description={p?.number}
          crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.purchasing"), href: "/admin/purchasing" }, { label: p?.number ?? "...", href: `/admin/purchasing/orders/${id}` }, { label: t("title") }]}
          actions={
            <>
              <Button asChild variant="secondary">
                <Link href={`/admin/purchasing/orders/${id}`}>
                  <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
                </Link>
              </Button>
              <Button onClick={() => window.print()} disabled={!p} data-testid="print-now">
                <Printer aria-hidden /> {t("printNow")}
              </Button>
            </>
          }
        />
      </div>
      {query.isPending ? <Skeleton className="h-96" aria-busy="true" /> : null}
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      {p ? (
        <article className="rounded-card border border-line bg-card p-5 print:rounded-none print:border-0 print:p-0" data-testid="po-sheet" data-number={p.number}>
          <header className="flex flex-wrap items-start justify-between gap-3 border-b border-line pb-4">
            <div>
              <h2 className="text-xl font-bold text-strong">{t("sheetTitle")}</h2>
              <p className="font-mono text-lg font-semibold text-strong">{p.number}</p>
              <p className="mt-1 text-sm">
                <PoStatusBadge status={p.status} />
              </p>
            </div>
            <dl className="text-sm sm:text-end">
              <div>
                <dt className="inline text-muted">{t("created")}: </dt>
                <dd className="inline text-strong">{dateTime(p.createdAt)}</dd>
              </div>
              {p.expectedDate ? (
                <div>
                  <dt className="inline text-muted">{t("expected")}: </dt>
                  <dd className="inline text-strong">{dateOnly(p.expectedDate)}</dd>
                </div>
              ) : null}
              {p.supplierInvoiceNumber ? (
                <div>
                  <dt className="inline text-muted">{t("invoice")}: </dt>
                  <dd className="inline font-mono text-strong">{p.supplierInvoiceNumber}</dd>
                </div>
              ) : null}
              {p.dueDate ? (
                <div>
                  <dt className="inline text-muted">{t("due")}: </dt>
                  <dd className="inline text-strong">{dateOnly(p.dueDate)}</dd>
                </div>
              ) : null}
            </dl>
          </header>

          <div className="grid gap-4 border-b border-line py-4 sm:grid-cols-2">
            <PartyBlock title={t("buyer")} party={p.buyer} />
            <PartyBlock title={t("supplier")} party={p.supplier} />
          </div>

          <table className="mt-4 w-full table-fixed border-collapse text-sm">
            <caption className="sr-only">{t("linesCaption")}</caption>
            <thead>
              <tr className="border-b-2 border-strong">
                <th scope="col" className="w-[9%] py-2 pe-2 text-start font-semibold">{t("columns.no")}</th>
                <th scope="col" className="w-[41%] py-2 pe-2 text-start font-semibold">{t("columns.item")}</th>
                <th scope="col" className="w-[10%] py-2 pe-2 text-end font-semibold">{t("columns.qty")}</th>
                <th scope="col" className="w-[20%] py-2 pe-2 text-end font-semibold">{t("columns.unit")}</th>
                <th scope="col" className="w-[20%] py-2 text-end font-semibold">{t("columns.total")}</th>
              </tr>
            </thead>
            <tbody>
              {p.lines.map((l) => (
                <tr key={l.no} className="border-b border-line align-top print:break-inside-avoid" data-testid="sheet-row">
                  <td className="py-2 pe-2 tabular-nums">{toNumber(l.no)}</td>
                  <td className="py-2 pe-2 [overflow-wrap:anywhere]">
                    <span className="block font-medium text-strong">{l.description}</span>
                    <span className="block font-mono text-xs text-muted">{l.sku}</span>
                  </td>
                  <td className="py-2 pe-2 text-end tabular-nums">{toNumber(l.quantity)}</td>
                  <td className="py-2 pe-2 text-end tabular-nums [overflow-wrap:anywhere]">{money(l.unitCost)}</td>
                  <td className="py-2 text-end tabular-nums [overflow-wrap:anywhere]">{money(l.lineTotal)}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th scope="row" colSpan={4} className="pt-3 pe-2 text-end font-semibold">{t("grandTotal")}</th>
                <td className="pt-3 text-end text-base font-bold tabular-nums text-strong [overflow-wrap:anywhere]" data-testid="sheet-total">{money(p.total)}</td>
              </tr>
            </tfoot>
          </table>

          {p.notes ? (
            <p className="mt-4 text-sm text-muted [overflow-wrap:anywhere]">
              <span className="font-semibold text-strong">{t("notes")}: </span>
              {p.notes}
            </p>
          ) : null}
          <div className="mt-10 grid gap-8 text-sm sm:grid-cols-2">
            <p className="border-t border-strong pt-1">{t("signBuyer")}</p>
            <p className="border-t border-strong pt-1">{t("signSupplier")}</p>
          </div>
        </article>
      ) : null}
    </>
  );
}
