"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { RotateCcw } from "lucide-react";
import { buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";

/** Order states from which a return may be asked for (the request page checks the rules again and explains a refusal). */
const RETURNABLE_ORDER_STATUSES = new Set(["Delivered", "ReturnInProgress"]);

/** The route of the customer's return request for one order. */
export const returnRequestHref = (orderId: string) => `/account/returns/new?order=${encodeURIComponent(orderId)}`;

/**
 * "Return or exchange items": the link the customer order page shows for a delivered order. It leads to `/account/returns/new?order=<id>`.
 * Pass `orderStatus` to hide it for orders that cannot be returned.
 */
export function ReturnOrderLink({ orderId, orderStatus, variant = "secondary", className }: { orderId: string; orderStatus?: string; variant?: "primary" | "secondary" | "ghost"; className?: string }) {
  const t = useTranslations("returns.orderLink");
  if (orderStatus && !RETURNABLE_ORDER_STATUSES.has(orderStatus)) return null;
  return (
    <Link href={returnRequestHref(orderId)} className={cn(buttonVariants({ variant }), className)} data-testid="return-order-link">
      <RotateCcw aria-hidden /> {t("label")}
    </Link>
  );
}
