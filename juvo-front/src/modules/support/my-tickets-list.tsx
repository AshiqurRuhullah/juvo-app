"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Headset, Plus } from "lucide-react";
import { buttonVariants } from "@/components/ui/button";
import { Pagination } from "@/components/ui/pagination";
import { usePaginationLabels } from "@/components/data/list-kit";
import { PAGE_SIZE } from "@/lib/pagination";
import { Alert, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { toNumber } from "@/modules/catalog/format";
import { useMyTickets } from "./api";
import { StatusBadge, useCategoryLabel } from "./common";
import { TICKET_STATUSES, type TicketStatus } from "./logic";


/** The customer's tickets, newest activity first: a status filter on top and cards at every width. */
export function MyTicketsList() {
  const t = useTranslations("support.mine");
  const ts = useTranslations("support.status");
  const tc = useTranslations("support.common");
  const dateTime = useDateTime();
  const categoryLabel = useCategoryLabel();
  const [status, setStatus] = React.useState<TicketStatus | undefined>(undefined);
  const pagerLabels = usePaginationLabels();
  const [page, setPage] = React.useState(1);
  const query = useMyTickets({ status, page, pageSize: PAGE_SIZE });
  const items = query.data?.items ?? [];
  const total = toNumber(query.data?.total);
  const choose = (next: TicketStatus | undefined) => {
    setStatus(next);
    setPage(1);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div role="group" aria-label={t("filterLabel")} className="flex flex-wrap gap-1 rounded-control bg-sunken p-1">
          {[undefined, ...TICKET_STATUSES].map((s) => (
            <button
              key={s ?? "all"}
              type="button"
              aria-pressed={status === s}
              onClick={() => choose(s)}
              className={cn("min-h-11 min-w-11 rounded-lg px-3 text-base font-semibold transition-colors", status === s ? "bg-card text-strong shadow-sm" : "text-muted [@media(hover:hover)]:hover:text-strong")}
            >
              {s ? ts(s) : t("all")}
            </button>
          ))}
        </div>
        <Link href="/account/support/new" className={buttonVariants({ variant: "primary" })} data-testid="new-request">
          <Plus aria-hidden /> {t("new")}
        </Link>
      </div>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {query.isPending ? (
        <div className="space-y-3" aria-busy="true">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : null}
      {!query.isPending && items.length === 0 && !query.isError ? (
        <Card>
          <EmptyState icon={<Headset aria-hidden />} title={status ? t("emptyFiltered") : t("empty")} description={t("emptyHelp")} />
        </Card>
      ) : null}

      <ul className="space-y-3" aria-label={t("title")}>
        {items.map((item) => (
          <li key={item.id}>
            <Card className="p-4" data-testid="my-ticket">
              <div className="space-y-1">
                <div className="flex flex-wrap items-center gap-2">
                  <Link href={`/account/support/${item.id}`} className="inline-flex min-h-11 items-center rounded-control font-mono font-semibold text-strong hover:text-primary hover:underline">
                    {item.number}
                  </Link>
                  <StatusBadge status={item.status} />
                </div>
                <p className="font-medium text-strong [overflow-wrap:anywhere]">{item.subject}</p>
                <p className="text-sm text-muted">
                  {categoryLabel(item.category)}
                  {item.orderNumber ? ` · ${t("order", { number: item.orderNumber })}` : ""} · {t("updated", { date: dateTime(item.lastActivityAt) })}
                </p>
              </div>
            </Card>
          </li>
        ))}
      </ul>

      <Pagination page={page} total={total} onPageChange={setPage} labels={pagerLabels} />
    </div>
  );
}
