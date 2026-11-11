"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ChevronLeft, ChevronRight, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, Badge, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useMyReturns, type MyReturnsFilter } from "./api";
import { ReturnStatusBadge, Thumb } from "./common";

const PAGE_SIZE = 10;

/** The customer's returns: active ones first, closed ones on the other tab. Cards at every width (a return has few columns worth showing). */
export function MyReturnsList() {
  const t = useTranslations("returns.mine");
  const tc = useTranslations("returns.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const [filter, setFilter] = React.useState<MyReturnsFilter>("active");
  const [page, setPage] = React.useState(1);
  const query = useMyReturns({ filter, page, pageSize: PAGE_SIZE });
  const items = query.data?.items ?? [];
  const total = toNumber(query.data?.total);
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <div className="space-y-4">
      <div role="group" aria-label={t("filterLabel")} className="grid grid-cols-2 gap-1 rounded-control bg-sunken p-1 sm:max-w-sm">
        {(["active", "closed"] as const).map((f) => (
          <button
            key={f}
            type="button"
            aria-pressed={filter === f}
            onClick={() => {
              setFilter(f);
              setPage(1);
            }}
            className={cn("min-h-11 rounded-lg px-3 text-base font-semibold transition-colors", filter === f ? "bg-card text-strong shadow-sm" : "text-muted [@media(hover:hover)]:hover:text-strong")}
          >
            {t(`filter.${f}`)}
          </button>
        ))}
      </div>

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {query.isPending ? (
        <div className="space-y-3" aria-busy="true">
          <Skeleton className="h-28" />
          <Skeleton className="h-28" />
        </div>
      ) : null}

      {!query.isPending && items.length === 0 && !query.isError ? (
        <Card>
          <EmptyState icon={<RotateCcw aria-hidden />} title={filter === "active" ? t("emptyActive") : t("emptyClosed")} description={t("emptyHelp")} />
        </Card>
      ) : null}

      <ul className="space-y-3" aria-label={t("title")}>
        {items.map((item) => (
          <li key={item.id}>
            <Card className="p-4" data-testid="my-return">
              <div className="flex items-start gap-3">
                <Thumb src={item.firstItemImageUrl} size={64} />
                <div className="min-w-0 flex-1 space-y-1">
                  <div className="flex flex-wrap items-center gap-2">
                    <Link href={`/account/returns/${item.id}`} className="inline-flex min-h-11 items-center rounded-control font-mono font-semibold text-strong hover:text-primary hover:underline">
                      {item.number}
                    </Link>
                    <ReturnStatusBadge status={item.status} />
                    <Badge variant="outline">{t(`kind.${item.kind}`)}</Badge>
                  </div>
                  <p className="text-strong [overflow-wrap:anywhere]">{t("summary", { name: item.firstItemName ?? "-", count: toNumber(item.quantity) })}</p>
                  <p className="text-sm text-muted">
                    {t("order", { number: item.orderNumber })} · {dateTime(item.requestedAt)}
                  </p>
                  {item.kind === "Return" ? <p className="text-sm text-muted">{t("refundAbout", { amount: money(item.refund) })}</p> : null}
                </div>
              </div>
            </Card>
          </li>
        ))}
      </ul>

      {pages > 1 ? (
        <nav className="flex items-center justify-between gap-3" aria-label={t("paging")}>
          <Button variant="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            <ChevronLeft className="rtl:rotate-180" aria-hidden /> {tc("previous")}
          </Button>
          <span className="text-sm text-muted">{tc("pageOf", { page, pages })}</span>
          <Button variant="secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            {tc("next")} <ChevronRight className="rtl:rotate-180" aria-hidden />
          </Button>
        </nav>
      ) : null}
    </div>
  );
}
