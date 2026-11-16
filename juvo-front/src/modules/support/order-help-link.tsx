"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { Headset } from "lucide-react";
import { buttonVariants } from "@/components/ui/button";

/** "Get help with this order" on the customer's order page: leads to a new request with the order already chosen. */
export function OrderHelpLink({ orderId }: { orderId: string }) {
  const t = useTranslations("support.orderLink");
  return (
    <Link href={`/account/support/new?order=${encodeURIComponent(orderId)}`} className={buttonVariants({ variant: "secondary" })} data-testid="order-help-link">
      <Headset aria-hidden /> {t("label")}
    </Link>
  );
}
