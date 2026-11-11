"use client";

import * as React from "react";
import Image from "next/image";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ChevronLeft, ChevronRight, ClipboardList } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { mediaUrl, toNumber, useMoney } from "@/modules/catalog/format";
import { CUSTOMER_FILTERS, useMyOrders, useOrderErrorText, type CustomerFilter } from "./api";
import { DeliveryStatusBadge, OrderStatusBadge, PaymentStatusBadge } from "./common";

const PAGE_SIZE = 10;

/** The customer's orders, newest first, filtered by what they are waiting for. Each row is a card that opens the order. */
export function MyOrders() {
  const t = useTranslations("orders.mine");
  const money = useMoney();
  const dateTime = useDateTime();
  const errorText = useOrderErrorText();
  const [filter, setFilter] = React.useState<CustomerFilter>("active");
  const [page, setPage] = React.useState(1);
  const query = useMyOrders({ filter, page, pageSize: PAGE_SIZE });
  const total = toNumber(query.data?.total);
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE));
  const items = query.data?.items ?? [];

  return (
    <div className="space-y-4">
      <div role="group" aria-label={t("filters")} className="grid grid-cols-2 gap-1 rounded-control bg-sunken p-1 sm:grid-cols-4">
        {CUSTOMER_FILTERS.map((value) => (
          <button
            key={value}
            type="button"
            aria-pressed={filter === value}
            onClick={() => {
              setFilter(value);
              setPage(1);
            }}
            className={cn("min-h-11 rounded-lg px-3 text-base font-semibold transition-colors", filter === value ? "bg-card text-strong shadow-sm" : "text-muted [@media(hover:hover)]:hover:text-strong")}
            data-testid={`filter-${value}`}
          >
            {t(`filter.${value}`)}
          </button>
        ))}
      </div>

      {query.isError ? <Alert variant="danger">{errorText(query.error)}</Alert> : null}
      {query.isLoading ? <Skeleton className="h-40" aria-busy /> : null}

      {!query.isLoading && !query.isError && items.length === 0 ? (
        <EmptyState
          icon={<ClipboardList aria-hidden />}
          title={t(`empty.${filter}`)}
          description={filter === "active" ? t("empty.help") : undefined}
          action={
            filter === "active" ? (
              <Link href="/shop" className={buttonVariants()}>
                {t("empty.shop")}
              </Link>
            ) : undefined
          }
        />
      ) : null}

      <ul className="space-y-3" aria-label={t("list")} data-testid="my-orders">
        {items.map((order) => {
          const image = mediaUrl(order.firstItemImageUrl);
          const count = toNumber(order.itemCount);
          return (
            <li key={order.id}>
              <Link href={`/account/orders/${order.id}`} className="flex gap-3 rounded-card border border-line bg-card p-3 transition-colors hover:border-primary sm:p-4" data-testid="my-order" data-number={order.number}>
                <span className="relative block aspect-[4/5] w-16 shrink-0 overflow-hidden rounded-control bg-sunken sm:w-20">{image ? <Image src={image} alt="" fill sizes="80px" className="object-cover" /> : null}</span>
                <span className="min-w-0 flex-1 space-y-1.5">
                  <span className="flex flex-wrap items-baseline justify-between gap-x-3">
                    <span className="font-mono font-semibold text-strong [overflow-wrap:anywhere]">{order.number}</span>
                    <span className="font-semibold text-strong">{money(order.grandTotal)}</span>
                  </span>
                  <span className="block text-sm text-muted [overflow-wrap:anywhere]">
                    {order.firstItemName}
                    {count > 1 ? ` ${t("more", { count: count - 1 })}` : ""}
                  </span>
                  <span className="block text-sm text-muted">{dateTime(order.placedAt)}</span>
                  <span className="flex flex-wrap gap-1.5">
                    <OrderStatusBadge status={order.status} />
                    <PaymentStatusBadge status={order.paymentStatus} />
                    <DeliveryStatusBadge status={order.deliveryStatus} />
                  </span>
                </span>
                <ChevronRight className="my-auto size-5 shrink-0 text-muted rtl:rotate-180" aria-hidden />
              </Link>
            </li>
          );
        })}
      </ul>

      {pages > 1 ? (
        <nav className="flex items-center justify-between gap-2" aria-label={t("paging")}>
          <Button variant="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            <ChevronLeft className="rtl:rotate-180" aria-hidden /> {t("previous")}
          </Button>
          <span className="text-sm text-muted">{t("pageOf", { page, pages })}</span>
          <Button variant="secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            {t("next")} <ChevronRight className="rtl:rotate-180" aria-hidden />
          </Button>
        </nav>
      ) : null}
    </div>
  );
}
