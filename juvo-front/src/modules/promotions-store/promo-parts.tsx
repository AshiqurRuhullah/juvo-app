"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { useQueryClient } from "@tanstack/react-query";
import { Tag, Zap } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { useMoney } from "@/modules/catalog/format";
import { Countdown } from "./countdown";
import { priceView, promotionLabel, type PromotionInfoLike } from "./logic";

let lastRefresh = 0;

/**
 * What to do when a countdown reaches zero: ask the API for the real prices again (cards, product page, cart, checkout) and re-render
 * the server pages once. Many cards can end at the same second, so this runs at most once every three seconds.
 */
export function useOnPromotionEnded() {
  const router = useRouter();
  const queryClient = useQueryClient();
  return React.useCallback(() => {
    const now = Date.now();
    if (now - lastRefresh < 3000) return;
    lastRefresh = now;
    void queryClient.invalidateQueries({ queryKey: ["store"] });
    void queryClient.invalidateQueries({ queryKey: ["orders"] });
    router.refresh();
  }, [queryClient, router]);
}

/** The promotion's own label ("Eid flash") as a chip. A flash sale gets a bolt, other offers a tag. Nothing when the API sent no promotion. */
export function PromoBadge({ promotion, className }: { promotion: (PromotionInfoLike & { type?: string }) | null | undefined; className?: string }) {
  const locale = useLocale();
  const label = promotionLabel(promotion, locale);
  if (!promotion || !label) return null;
  const Icon = promotion.type === "FlashSale" ? Zap : Tag;
  return (
    <Badge variant="brand" className={cn("max-w-full bg-card/95", className)} data-testid="promo-badge">
      <Icon className="size-3 shrink-0" aria-hidden />
      <span className="truncate">{label}</span>
    </Badge>
  );
}

/** "You save ৳200" under a price, only when the price is below the list price. */
export function SavingLine({ price, regular, className }: { price: number | string; regular: number | string; className?: string }) {
  const t = useTranslations("storepromo.card");
  const money = useMoney();
  const view = priceView(price, regular);
  if (!view.onSale) return null;
  return (
    <p className={cn("text-sm font-medium text-success-ink", className)} data-testid="you-save">
      {t("youSave", { amount: money(view.saving) })}
    </p>
  );
}

/** The countdown of a promotion that has an end time (only flash sales and campaigns carry one); refreshes the prices when it ends. */
export function PromotionCountdown({ promotion, size, className }: { promotion: { endsAt?: string | null } | null | undefined; size?: "sm" | "lg"; className?: string }) {
  const onEnded = useOnPromotionEnded();
  if (!promotion?.endsAt) return null;
  return <Countdown endsAt={promotion.endsAt} size={size} className={className} onEnded={onEnded} />;
}
