"use client";

import * as React from "react";
import Image from "next/image";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Heart, ShoppingBag, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useCartErrorText } from "@/modules/cart/use-cart-error";
import { mediaUrl, toNumber, useMoney } from "@/modules/catalog/format";
import { useMoveToCart, useRemoveFromWishlist, useStoreProduct, useWishlist, type WishlistItem } from "@/modules/store/api";
import { usePickName } from "@/modules/store/hooks";

/** Mounted only while the dialog is open: loads the product's colours and sizes and moves the chosen one to the cart. */
function MoveBody({ item, onDone }: { item: WishlistItem; onDone: () => void }) {
  const t = useTranslations("myaccount.wishlist");
  const money = useMoney();
  const errorText = useCartErrorText();
  const product = useStoreProduct(item.slug);
  const move = useMoveToCart();
  const [variantId, setVariantId] = React.useState("");
  const [quantity, setQuantity] = React.useState("1");
  const [error, setError] = React.useState<string | null>(null);

  if (product.isPending) return <Skeleton className="mt-4 h-32" />;
  if (product.isError) return <Alert variant="danger" className="mt-4">{errorText(product.error)}</Alert>;

  const variants = product.data.variants ?? [];
  const label = (v: (typeof variants)[number]) => [v.colorName, v.size].filter(Boolean).join(" · ") || v.sku;
  const chosen = variants.find((v) => v.id === variantId);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const qty = Math.floor(Number(quantity));
    if (!variantId) return setError(t("pickVariant"));
    if (!Number.isFinite(qty) || qty < 1) return setError(t("pickQuantity"));
    setError(null);
    try {
      await move.mutateAsync({ productId: item.productId, variantId, quantity: qty });
      toast.success(t("moved"));
      onDone();
    } catch (failure) {
      setError(errorText(failure));
    }
  };

  return (
    <form onSubmit={submit} noValidate className="mt-4 space-y-4">
      <Field label={t("variant")} htmlFor="move-variant" required>
        <Select
          id="move-variant"
          value={variantId}
          onValueChange={setVariantId}
          options={variants.map((v) => ({ value: v.id, label: `${label(v)} — ${money(v.price)}`, disabled: !v.available || !v.inStock, hint: !v.inStock ? t("soldOut") : v.lowStock ? t("fewLeft") : undefined }))}
          placeholder={t("choose")}
          searchPlaceholder={t("search")}
          emptyText={t("noMatch")}
        />
      </Field>
      <Field label={t("quantity")} htmlFor="move-quantity">
        <Input id="move-quantity" type="number" inputMode="numeric" min={1} value={quantity} onChange={(e) => setQuantity(e.target.value)} />
      </Field>
      {chosen ? <p className="text-sm text-muted">{t("price", { price: money(chosen.price) })}</p> : null}
      {error ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {error}
        </p>
      ) : null}
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="secondary" onClick={onDone} disabled={move.isPending}>
          {t("cancel")}
        </Button>
        <Button type="submit" loading={move.isPending}>
          <ShoppingBag aria-hidden /> {t("addToCart")}
        </Button>
      </div>
    </form>
  );
}

function WishlistCard({ item, onMove }: { item: WishlistItem; onMove: () => void }) {
  const t = useTranslations("myaccount.wishlist");
  const money = useMoney();
  const pick = usePickName();
  const errorText = useCartErrorText();
  const remove = useRemoveFromWishlist();
  const name = pick(item.name, item.nameBn);
  const image = mediaUrl(item.imageUrl);
  const price = toNumber(item.price);
  const regular = toNumber(item.regularPrice);

  return (
    <Card className="flex h-full flex-col overflow-hidden" data-testid="wishlist-item">
      <Link href={`/product/${item.slug}`} className="relative block aspect-[4/5] bg-sunken" aria-label={name}>
        {image ? <Image src={image} alt="" fill sizes="(min-width: 1024px) 25vw, 50vw" className="object-cover" /> : null}
        {!item.available ? <Badge variant="neutral" className="absolute start-2 top-2">{t("unavailable")}</Badge> : null}
      </Link>
      <div className="flex flex-1 flex-col gap-2 p-3">
        <Link href={`/product/${item.slug}`} className="line-clamp-2 font-medium text-strong hover:text-primary [overflow-wrap:anywhere]">
          {name}
        </Link>
        <p className="flex items-baseline gap-2">
          <span className="font-semibold text-strong">{money(price)}</span>
          {regular > price ? <span className="text-sm text-muted line-through">{money(regular)}</span> : null}
        </p>
        <div className="mt-auto flex flex-col gap-2">
          <Button onClick={onMove} disabled={!item.available}>
            <ShoppingBag aria-hidden /> {t("moveToCart")}
          </Button>
          <Button
            variant="ghost"
            className="text-danger hover:text-danger"
            loading={remove.isPending}
            aria-label={t("removeAria", { name })}
            onClick={() => remove.mutate(item.productId, { onSuccess: () => toast(t("removed", { name })), onError: (failure) => toast.error(errorText(failure)) })}
          >
            <Trash2 aria-hidden /> {t("remove")}
          </Button>
        </div>
      </div>
    </Card>
  );
}

export function WishlistView() {
  const t = useTranslations("myaccount.wishlist");
  const errorText = useCartErrorText();
  const wishlist = useWishlist();
  const [moving, setMoving] = React.useState<WishlistItem | null>(null);

  if (wishlist.isPending) {
    return (
      <div className="grid grid-cols-2 gap-4 lg:grid-cols-4">
        {[0, 1, 2, 3].map((i) => (
          <Skeleton key={i} className="h-72" />
        ))}
      </div>
    );
  }
  if (wishlist.isError) return <Alert variant="danger">{errorText(wishlist.error)}</Alert>;

  const items = wishlist.data;
  if (items.length === 0) {
    return (
      <Card>
        <EmptyState
          icon={<Heart aria-hidden />}
          title={t("empty.title")}
          description={t("empty.text")}
          action={
            <Link href="/shop" className="inline-flex min-h-11 items-center rounded-control bg-primary px-5 font-medium text-on-primary">
              {t("empty.cta")}
            </Link>
          }
        />
      </Card>
    );
  }

  return (
    <>
      <ul className="grid grid-cols-2 gap-3 sm:gap-4 lg:grid-cols-4" aria-label={t("listLabel")}>
        {items.map((item) => (
          <li key={item.productId}>
            <WishlistCard item={item} onMove={() => setMoving(item)} />
          </li>
        ))}
      </ul>
      <Dialog open={!!moving} onOpenChange={(next) => !next && setMoving(null)}>
        <ModalContent size="sm" closeLabel={t("close")}>
          <DialogTitle className="font-display text-xl font-semibold text-strong">{t("moveTitle")}</DialogTitle>
          <DialogDescription className="mt-1 text-muted">{moving?.name}</DialogDescription>
          {moving ? <MoveBody item={moving} onDone={() => setMoving(null)} /> : null}
        </ModalContent>
      </Dialog>
    </>
  );
}
