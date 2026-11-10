"use client";

import * as React from "react";
import Image from "next/image";
import { useTranslations } from "next-intl";
import { Heart, ImageOff, ShoppingBag } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { discountPercent, useMoney } from "./format";

export type CardSwatch = { name: string; hex: string | null };
export type CardSize = { label: string; soldOut: boolean };

export type ProductCardData = {
  name: string;
  nameBn?: string | null;
  tagline?: string | null;
  /** Card picture (800 x 1000 rendition) and, for the hover picture, the next one. */
  imageUrl?: string;
  hoverImageUrl?: string;
  blurDataUrl?: string | null;
  regularPrice: number;
  /** The price a customer pays right now (the sale price inside its dates). */
  effectivePrice: number;
  isNew?: boolean;
  /** Stock-based labels. Unknown (undefined) in places where stock is not shown. */
  lowStock?: boolean;
  soldOut?: boolean;
  swatches: CardSwatch[];
  sizes: CardSize[];
};

/**
 * The premium product card (SRS 3.4), built only from the data entered for the product: framed 4:5 picture (the second picture shows
 * on hover), name, price with the old price struck through and an automatic discount badge, colour swatches, size chips with sold-out
 * sizes crossed out, and automatic badges (New, Sale, Low stock, Sold out). The storefront uses the same card (Step 7); in the admin it
 * is the live preview, so its buttons only look the part.
 */
export function ProductCard({ data, preview = false, className }: { data: ProductCardData; preview?: boolean; className?: string }) {
  const t = useTranslations("catalog.card");
  const money = useMoney();
  const percent = discountPercent(data.regularPrice, data.effectivePrice);
  const onSale = percent > 0;

  return (
    <article className={cn("group w-full max-w-sm overflow-hidden rounded-card border border-line bg-card shadow-card", className)} aria-label={data.name || t("untitled")}>
      <div className="relative aspect-[4/5] overflow-hidden bg-sunken">
        {data.imageUrl ? (
          <>
            <Image
              src={data.imageUrl}
              alt={data.name}
              fill
              unoptimized
              sizes="(min-width: 1024px) 20rem, 90vw"
              placeholder={data.blurDataUrl ? "blur" : "empty"}
              blurDataURL={data.blurDataUrl ?? undefined}
              className={cn("object-cover transition-opacity duration-300", data.hoverImageUrl && "[@media(hover:hover)]:group-hover:opacity-0")}
            />
            {data.hoverImageUrl ? <Image src={data.hoverImageUrl} alt="" fill unoptimized sizes="(min-width: 1024px) 20rem, 90vw" className="object-cover opacity-0 transition-opacity duration-300 [@media(hover:hover)]:group-hover:opacity-100" /> : null}
          </>
        ) : (
          <div className="flex size-full flex-col items-center justify-center gap-2 text-muted">
            <ImageOff className="size-10" aria-hidden />
            <span className="text-sm">{t("noPicture")}</span>
          </div>
        )}

        <div className="absolute start-3 top-3 flex flex-col items-start gap-1.5">
          {data.isNew ? <Badge variant="neutral" className="bg-card/95">{t("new")}</Badge> : null}
          {onSale ? <Badge variant="sale">{t("sale", { percent })}</Badge> : null}
          {data.soldOut ? <Badge variant="neutral" className="bg-card/95">{t("soldOut")}</Badge> : data.lowStock ? <Badge variant="warning" className="bg-card/95">{t("lowStock")}</Badge> : null}
        </div>

        <span className="absolute end-3 top-3 flex size-11 items-center justify-center rounded-full bg-card/95 text-strong shadow" aria-hidden={preview || undefined}>
          <Heart className="size-5" aria-hidden />
          {preview ? <span className="sr-only">{t("wishlist")}</span> : null}
        </span>
      </div>

      <div className="space-y-3 p-4">
        <div className="space-y-0.5">
          <h3 className="font-display text-lg font-semibold leading-snug text-strong [overflow-wrap:anywhere]">{data.name || t("untitled")}</h3>
          {data.nameBn ? <p className="text-sm text-muted [overflow-wrap:anywhere]">{data.nameBn}</p> : null}
          {data.tagline ? <p className="line-clamp-2 text-sm text-muted [overflow-wrap:anywhere]">{data.tagline}</p> : null}
        </div>

        <p className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5">
          <span className="text-lg font-semibold text-strong">{money(data.effectivePrice)}</span>
          {onSale ? (
            <>
              <span className="text-sm text-muted line-through">
                <span className="sr-only">{t("wasPrice")} </span>
                {money(data.regularPrice)}
              </span>
            </>
          ) : null}
        </p>

        {data.swatches.length > 0 ? (
          <ul className="flex flex-wrap gap-1.5" aria-label={t("colours")}>
            {data.swatches.map((swatch) => (
              <li key={swatch.name} title={swatch.name}>
                <span className="block size-5 rounded-full border border-line shadow-inner" style={{ backgroundColor: swatch.hex ?? "var(--surface-sunken)" }} />
                <span className="sr-only">{swatch.name}</span>
              </li>
            ))}
          </ul>
        ) : null}

        {data.sizes.length > 0 ? (
          <ul className="flex flex-wrap gap-1.5" aria-label={t("sizes")}>
            {data.sizes.map((size) => (
              <li key={size.label} className={cn("rounded-md border px-2 py-0.5 text-xs font-semibold", size.soldOut ? "border-line text-muted line-through" : "border-line text-strong")}>
                {size.label}
                {size.soldOut ? <span className="sr-only"> ({t("soldOut")})</span> : null}
              </li>
            ))}
          </ul>
        ) : null}

        <div
          className="flex min-h-11 items-center justify-center gap-2 rounded-control bg-primary px-4 text-base font-semibold text-on-primary opacity-90"
          role={preview ? "note" : undefined}
        >
          <ShoppingBag className="size-5" aria-hidden />
          {t("addToCart")}
        </div>
      </div>
    </article>
  );
}
