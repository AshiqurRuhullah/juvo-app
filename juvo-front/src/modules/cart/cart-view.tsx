"use client";

import * as React from "react";
import Image from "next/image";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Lock, Minus, Plus, RotateCcw, ShieldCheck, ShoppingBag, Trash2, Wallet } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { Alert, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { cn } from "@/lib/utils";
import { mediaUrl, toNumber, useMoney } from "@/modules/catalog/format";
import { useAddToCart, useCart, useRemoveCartLine, useSetCartQuantity, type CartLine, type DeliveryQuote } from "@/modules/store/api";
import { usePickName } from "@/modules/store/hooks";
import { CouponBox } from "./coupon-box";
import { DeliveryEstimate } from "./delivery-estimate";
import { useCartErrorText } from "./use-cart-error";

function LineItem({ line }: { line: CartLine }) {
  const t = useTranslations("cart");
  const money = useMoney();
  const pick = usePickName();
  const errorText = useCartErrorText();
  const setQuantity = useSetCartQuantity();
  const remove = useRemoveCartLine();
  const add = useAddToCart();

  const quantity = toNumber(line.quantity);
  const max = toNumber(line.maxQuantity);
  const unavailable = line.problem === "OUT_OF_STOCK";
  const name = pick(line.name, line.nameBn);
  const image = mediaUrl(line.imageUrl);
  const busy = setQuantity.isPending || remove.isPending;

  const change = async (next: number) => {
    try {
      await setQuantity.mutateAsync({ variantId: line.variantId, quantity: next });
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  const removeLine = async () => {
    try {
      await remove.mutateAsync(line.variantId);
      toast(t("removed", { name }), {
        action: {
          label: t("undo"),
          onClick: () => add.mutate({ variantId: line.variantId, quantity: Math.max(1, quantity) }, { onError: (failure) => toast.error(errorText(failure)) }),
        },
      });
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  return (
    <li className="flex gap-3 py-4 sm:gap-4" data-testid="cart-line" data-sku={line.sku}>
      <Link href={`/product/${line.slug}`} className="relative block aspect-[4/5] w-20 shrink-0 overflow-hidden rounded-control bg-sunken sm:w-24" tabIndex={-1} aria-hidden>
        {image ? <Image src={image} alt="" fill sizes="96px" className="object-cover" /> : null}
      </Link>
      <div className="min-w-0 flex-1 space-y-2">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <Link href={`/product/${line.slug}`} className="line-clamp-2 py-2.5 font-medium text-strong hover:text-primary [overflow-wrap:anywhere]">
              {name}
            </Link>
            <p className="text-sm text-muted">
              {[line.colorName, line.size ? t("size", { size: line.size }) : null].filter(Boolean).join(" · ")}
            </p>
          </div>
          <p className="shrink-0 text-end font-semibold text-strong" data-testid="line-total">
            {money(line.lineTotal)}
          </p>
        </div>

        <p className="flex flex-wrap items-baseline gap-x-2 text-sm">
          <span className="text-strong">{money(line.unitPrice)}</span>
          {line.priceChanged && line.previousUnitPrice != null ? <span className="text-muted line-through">{money(line.previousUnitPrice)}</span> : null}
          <span className="text-muted">{t("each")}</span>
        </p>
        {line.priceChanged ? <p className="text-sm font-medium text-warning-ink">{t("priceChanged")}</p> : null}
        {line.problem === "REDUCED" ? <p className="text-sm font-medium text-warning-ink">{t("reduced", { count: max })}</p> : null}
        {unavailable ? <p className="text-sm font-medium text-danger">{t("outOfStock")}</p> : null}

        <div className="flex flex-wrap items-center justify-between gap-2">
          {unavailable ? (
            <span />
          ) : (
            <div className="inline-flex items-center rounded-control border border-line bg-card" role="group" aria-label={t("quantityOf", { name })}>
              <Button variant="ghost" size="icon" aria-label={t("decrease")} disabled={busy || quantity <= 1} onClick={() => change(quantity - 1)}>
                <Minus aria-hidden />
              </Button>
              <output className="min-w-8 text-center font-medium text-strong" aria-live="polite" data-testid="line-quantity">
                {quantity}
              </output>
              <Button variant="ghost" size="icon" aria-label={t("increase")} disabled={busy || quantity >= max} onClick={() => change(quantity + 1)}>
                <Plus aria-hidden />
              </Button>
            </div>
          )}
          <Button variant="ghost" className="text-danger hover:text-danger" onClick={removeLine} disabled={busy} aria-label={t("removeLine", { name })}>
            <Trash2 aria-hidden /> {t("remove")}
          </Button>
        </div>
        {!unavailable && quantity >= max && max > 0 ? <p className="text-xs text-muted">{t("maxReached", { count: max })}</p> : null}
      </div>
    </li>
  );
}

export function CartView() {
  const t = useTranslations("cart");
  const money = useMoney();
  const errorText = useCartErrorText();
  const cart = useCart();
  const [quote, setQuote] = React.useState<DeliveryQuote | undefined>();

  if (cart.isPending) {
    return (
      <div className="grid grid-cols-[minmax(0,1fr)] gap-6 lg:grid-cols-[1fr_380px]" aria-busy>
        <Skeleton className="h-64" />
        <Skeleton className="h-64" />
      </div>
    );
  }
  if (cart.isError) return <Alert variant="danger">{errorText(cart.error)}</Alert>;

  const data = cart.data;
  const lines = data.lines ?? [];

  if (lines.length === 0) {
    return (
      <EmptyState
        icon={<ShoppingBag aria-hidden />}
        title={t("empty.title")}
        description={t("empty.text")}
        action={
          <Link href="/shop" className={buttonVariants({ size: "lg" })}>
            {t("empty.cta")}
          </Link>
        }
      />
    );
  }

  const subtotal = toNumber(data.subtotal);
  const discount = toNumber(data.discount);
  const delivery = quote ? toNumber(quote.total) : null;
  const total = toNumber(data.total) + (delivery ?? 0);
  const cartKey = `${subtotal}-${toNumber(data.weightGrams)}-${discount}`;

  return (
    <div className="grid grid-cols-[minmax(0,1fr)] gap-6 pb-24 lg:grid-cols-[1fr_380px] lg:pb-0 lg:gap-8">
      <div className="min-w-0 space-y-4">
        {data.hasProblems ? <Alert variant="warning">{t("hasProblems")}</Alert> : null}
        <Card className="px-4 sm:px-5">
          <ul className="divide-y divide-line" aria-label={t("linesLabel")}>
            {lines.map((line) => (
              <LineItem key={line.variantId} line={line} />
            ))}
          </ul>
        </Card>
        <Card className="p-4 sm:p-5">
          <DeliveryEstimate cartKey={cartKey} subtotal={subtotal - discount} onQuote={setQuote} />
        </Card>
      </div>

      <aside className="min-w-0 space-y-4 lg:sticky lg:top-24 lg:self-start" aria-label={t("summary.title")}>
        <Card className="space-y-4 p-4 sm:p-5">
          <h2 className="text-lg font-semibold text-strong">{t("summary.title")}</h2>
          <CouponBox coupon={data.coupon} />
          <dl className="space-y-2 border-t border-line pt-4 text-sm">
            <div className="flex justify-between gap-3">
              <dt className="text-muted">{t("summary.subtotal", { count: toNumber(data.itemCount) })}</dt>
              <dd className="text-strong" data-testid="summary-subtotal">
                {money(subtotal)}
              </dd>
            </div>
            {discount > 0 ? (
              <div className="flex justify-between gap-3 text-success-ink">
                <dt>{t("summary.discount")}</dt>
                <dd data-testid="summary-discount">-{money(discount)}</dd>
              </div>
            ) : null}
            <div className="flex justify-between gap-3">
              <dt className="text-muted">{t("summary.delivery")}</dt>
              <dd className="text-strong" data-testid="summary-delivery">
                {quote ? (quote.isFree ? t("summary.free") : money(quote.total)) : t("summary.chooseDelivery")}
              </dd>
            </div>
            <div className="flex justify-between gap-3 border-t border-line pt-3 text-base font-semibold">
              <dt className="text-strong">{t("summary.total")}</dt>
              <dd className="text-strong" data-testid="summary-total">
                {money(total)}
              </dd>
            </div>
          </dl>
          <div className="space-y-2">
            <Link href="/checkout" className={cn(buttonVariants({ size: "lg" }), "w-full")} data-testid="checkout-button">
              <Lock aria-hidden /> {t("checkout.button")}
            </Link>
            <p id="checkout-note" className="text-center text-sm text-muted" data-testid="checkout-note">
              {t("checkout.note")}
            </p>
          </div>
          <ul className="space-y-2 border-t border-line pt-4 text-sm text-muted">
            <li className="flex items-center gap-2">
              <Wallet className="size-4 shrink-0 text-primary" aria-hidden /> {t("trust.cod")}
            </li>
            <li className="flex items-center gap-2">
              <RotateCcw className="size-4 shrink-0 text-primary" aria-hidden /> {t("trust.returns")}
            </li>
            <li className="flex items-center gap-2">
              <ShieldCheck className="size-4 shrink-0 text-primary" aria-hidden /> {t("trust.secure")}
            </li>
          </ul>
        </Card>
      </aside>

      <div className={cn("fixed inset-x-0 bottom-0 z-30 flex items-center justify-between gap-3 border-t border-line bg-card/95 px-4 py-3 backdrop-blur lg:hidden")}>
        <div>
          <p className="text-xs text-muted">{t("summary.total")}</p>
          <p className="text-lg font-semibold text-strong">{money(total)}</p>
        </div>
        <Link href="/checkout" className={buttonVariants({ size: "lg" })} data-testid="checkout-button-bar">
          <Lock aria-hidden /> {t("checkout.button")}
        </Link>
      </div>
    </div>
  );
}
