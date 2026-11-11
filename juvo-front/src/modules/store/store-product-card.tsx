"use client";

import * as React from "react";
import Image from "next/image";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { ImageOff, ShoppingBag } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { api, unwrap } from "@/lib/api/client";
import { cn } from "@/lib/utils";
import { mediaUrl, toNumber, useMoney } from "@/modules/catalog/format";
import { useAddToCart, type StoreProductCard as CardData, type StoreProductDetail } from "./api";
import { usePickName, useStoreError } from "./hooks";
import { WishlistHeart } from "./wishlist-heart";

/**
 * The shop's product card (SRS 3.4): a framed 4:5 picture (the second picture shows on hover), badges (New, Sale, Low stock, Sold
 * out), name, price with the old price struck through, colour swatches and size chips (sold-out sizes crossed out). The whole card
 * opens the product page; the heart and the quick add are separate controls on top of it.
 */
export function StoreProductCard({ product, priority = false, className }: { product: CardData; priority?: boolean; className?: string }) {
  const t = useTranslations("store.card");
  const money = useMoney();
  const pick = usePickName();
  const router = useRouter();
  const storeError = useStoreError();
  const addToCart = useAddToCart();
  const [adding, setAdding] = React.useState(false);

  const name = pick(product.nameEn, product.nameBn);
  const image = mediaUrl(product.imageUrl);
  const hover = mediaUrl(product.hoverImageUrl);
  const percent = toNumber(product.discountPercent);
  const href = `/product/${product.slug}`;
  const single = product.swatches.length <= 1 && product.sizes.length <= 1;

  const quickAdd = async () => {
    setAdding(true);
    try {
      // The card does not know the variant id, so the one variant of a single-variant product is looked up first.
      const detail = unwrap(await api.GET("/api/v1/store/products/{slug}", { params: { path: { slug: product.slug } } })) as StoreProductDetail;
      const variant = detail.variants.find((v) => v.inStock);
      if (!variant) {
        toast.error(t("soldOutNow"));
        return;
      }
      await addToCart.mutateAsync({ variantId: variant.id, quantity: 1 });
      toast.success(t("added", { name }), { action: { label: t("viewCart"), onClick: () => router.push("/cart") } });
    } catch (error) {
      toast.error(storeError(error));
    } finally {
      setAdding(false);
    }
  };

  return (
    <article className={cn("group relative flex h-full flex-col overflow-hidden rounded-card border border-line bg-card shadow-card", className)} data-testid="product-card">
      <Link href={href} className="block rounded-t-card focus-visible:outline-offset-[-3px]">
        <span className="sr-only">{name}</span>
        <div className="relative aspect-[4/5] overflow-hidden bg-sunken">
          {image ? (
            <>
              <Image
                src={image}
                alt=""
                fill
                unoptimized
                priority={priority}
                sizes="(min-width: 1024px) 25vw, (min-width: 768px) 33vw, 50vw"
                placeholder={product.blurDataUrl ? "blur" : "empty"}
                blurDataURL={product.blurDataUrl ?? undefined}
                className={cn("object-cover transition-opacity duration-300", hover && "[@media(hover:hover)]:group-hover:opacity-0")}
              />
              {hover ? <Image src={hover} alt="" fill unoptimized sizes="(min-width: 1024px) 25vw, (min-width: 768px) 33vw, 50vw" className="object-cover opacity-0 transition-opacity duration-300 [@media(hover:hover)]:group-hover:opacity-100" /> : null}
            </>
          ) : (
            <div className="flex size-full flex-col items-center justify-center gap-2 text-muted">
              <ImageOff className="size-9" aria-hidden />
              <span className="text-sm">{t("noPicture")}</span>
            </div>
          )}
          <div className="absolute start-2.5 top-2.5 flex flex-col items-start gap-1.5">
            {product.isNew ? <Badge variant="neutral" className="bg-card/95">{t("new")}</Badge> : null}
            {product.onSale && percent > 0 ? <Badge variant="sale">{t("sale", { percent })}</Badge> : null}
            {product.soldOut ? <Badge variant="neutral" className="bg-card/95">{t("soldOut")}</Badge> : product.lowStock ? <Badge variant="warning" className="bg-card/95">{t("lowStock")}</Badge> : null}
          </div>
        </div>

        <div className="space-y-2 p-3.5 pb-2 sm:p-4 sm:pb-2">
          <h3 className="line-clamp-2 min-h-[2.75rem] text-base font-medium leading-snug text-strong [overflow-wrap:anywhere]">{name}</h3>
          <p className="flex flex-wrap items-baseline gap-x-2">
            <span className="text-lg font-semibold text-strong">{money(product.price)}</span>
            {product.onSale ? (
              <span className="text-sm text-muted line-through">
                <span className="sr-only">{t("wasPrice")} </span>
                {money(product.regularPrice)}
              </span>
            ) : null}
          </p>
          {product.swatches.length > 0 ? (
            <ul className="flex flex-wrap items-center gap-1.5" aria-label={t("colours")}>
              {product.swatches.slice(0, 5).map((swatch) => (
                <li key={swatch.name} title={swatch.name}>
                  <span className="block size-4 rounded-full border border-line" style={{ backgroundColor: swatch.hex ?? "var(--surface-sunken)" }} />
                  <span className="sr-only">{swatch.name}</span>
                </li>
              ))}
              {product.swatches.length > 5 ? <li className="text-xs text-muted">+{product.swatches.length - 5}</li> : null}
            </ul>
          ) : null}
          {product.sizes.length > 0 ? (
            <ul className="flex flex-wrap gap-1" aria-label={t("sizes")}>
              {product.sizes.slice(0, 6).map((size) => (
                <li key={size.label} className={cn("rounded-md border border-line px-1.5 py-0.5 text-xs font-medium", size.soldOut ? "text-muted line-through" : "text-strong")}>
                  {size.label}
                  {size.soldOut ? <span className="sr-only"> ({t("soldOut")})</span> : null}
                </li>
              ))}
              {product.sizes.length > 6 ? <li className="py-0.5 text-xs text-muted">+{product.sizes.length - 6}</li> : null}
            </ul>
          ) : null}
        </div>
      </Link>

      <div className="mt-auto p-3.5 pt-1.5 sm:p-4 sm:pt-1.5">
        {product.soldOut ? (
          <p className="flex min-h-11 items-center justify-center rounded-control bg-neutral-soft px-3 text-sm font-medium text-neutral-ink">{t("soldOut")}</p>
        ) : single ? (
          <button
            type="button"
            onClick={() => void quickAdd()}
            disabled={adding}
            className="flex min-h-11 w-full items-center justify-center gap-2 rounded-control border border-line bg-card px-3 text-sm font-medium text-strong transition-colors hover:bg-sunken disabled:opacity-60"
          >
            <ShoppingBag className="size-4" aria-hidden /> {adding ? t("adding") : t("add")}
          </button>
        ) : (
          <Link href={href} className="flex min-h-11 w-full items-center justify-center gap-2 rounded-control border border-line bg-card px-3 text-sm font-medium text-strong transition-colors hover:bg-sunken">
            {t("chooseOptions")}
          </Link>
        )}
      </div>

      <WishlistHeart productId={product.id} name={name} className="absolute end-2.5 top-2.5" />
    </article>
  );
}

/** Skeletons with the same shape as the cards, shown while a grid loads. */
export function ProductGridSkeleton({ count = 8 }: { count?: number }) {
  return (
    <ul className="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-3 xl:grid-cols-4" aria-hidden>
      {Array.from({ length: count }, (_, i) => (
        <li key={i} className="overflow-hidden rounded-card border border-line bg-card">
          <div className="skeleton aspect-[4/5]" />
          <div className="space-y-2 p-4">
            <div className="skeleton h-4 w-4/5 rounded" />
            <div className="skeleton h-5 w-1/3 rounded" />
            <div className="skeleton h-11 w-full rounded-control" />
          </div>
        </li>
      ))}
    </ul>
  );
}

/** The responsive grid: 2 columns on phones, 3 on tablets, 4 on desktops. */
export function ProductGrid({ products, priorityCount = 0 }: { products: CardData[]; priorityCount?: number }) {
  return (
    <ul className="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-3 xl:grid-cols-4">
      {products.map((product, index) => (
        <li key={product.id} className="min-w-0">
          <StoreProductCard product={product} priority={index < priorityCount} />
        </li>
      ))}
    </ul>
  );
}
