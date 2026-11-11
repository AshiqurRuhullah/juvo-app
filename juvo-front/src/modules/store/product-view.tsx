"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Check, Copy, Share2, Minus, MessageCircle, Plus, Ruler, ShoppingBag } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogDescription, DialogTitle, DialogTrigger, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { cn } from "@/lib/utils";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useAddToCart, useStoreProduct, type StoreProductDetail, type StoreVariant } from "./api";
import { usePickName, useStoreError } from "./hooks";
import { Breadcrumbs } from "./page-heading";
import { ProductDelivery } from "./product-delivery";
import { ProductGallery } from "./product-gallery";
import { RecentlyViewed, useRememberProduct } from "./recently-viewed";
import { ProductGrid } from "./store-product-card";
import { WishlistHeart } from "./wishlist-heart";

const MAX_QUANTITY = 10;

type SizeTable = { columns: string[]; rows: string[][] };

function parseSizeTable(json: string | null | undefined): SizeTable | null {
  if (!json) return null;
  try {
    const value = JSON.parse(json) as { columns?: unknown; rows?: unknown };
    const columns = Array.isArray(value.columns) ? value.columns.map(String) : [];
    const rows = Array.isArray(value.rows) ? value.rows.map((r) => (Array.isArray(r) ? r.map(String) : [])) : [];
    return columns.length > 0 && rows.length > 0 ? { columns, rows } : null;
  } catch {
    return null;
  }
}

const subscribeNothing = () => () => {};
const pageUrl = () => window.location.href;

function Share({ name }: { name: string }) {
  const t = useTranslations("store.share");
  const url = React.useSyncExternalStore(subscribeNothing, pageUrl, () => "");
  const [copied, setCopied] = React.useState(false);
  const link = "inline-flex min-h-11 items-center gap-2 rounded-control border border-line px-3 text-sm font-medium text-strong hover:bg-sunken";
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(url);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2000);
    } catch {
      toast.error(t("copyFailed"));
    }
  };
  return (
    <div className="flex flex-wrap items-center gap-2" role="group" aria-label={t("title")}>
      <span className="text-sm text-muted">{t("title")}</span>
      <a className={link} href={`https://www.facebook.com/sharer/sharer.php?u=${encodeURIComponent(url)}`} target="_blank" rel="noopener noreferrer">
        <Share2 className="size-4" aria-hidden /> {t("facebook")}
      </a>
      <a className={link} href={`https://wa.me/?text=${encodeURIComponent(`${name} ${url}`)}`} target="_blank" rel="noopener noreferrer">
        <MessageCircle className="size-4" aria-hidden /> {t("whatsapp")}
      </a>
      <button type="button" className={link} onClick={() => void copy()}>
        {copied ? <Check className="size-4 text-success-ink" aria-hidden /> : <Copy className="size-4" aria-hidden />} {copied ? t("copied") : t("copy")}
      </button>
    </div>
  );
}

function SizeGuide({ table }: { table: SizeTable }) {
  const t = useTranslations("store.product");
  return (
    <Dialog>
      <DialogTrigger asChild>
        <button type="button" className="inline-flex min-h-11 items-center gap-1.5 text-sm font-semibold text-primary hover:underline">
          <Ruler className="size-4" aria-hidden /> {t("sizeGuide")}
        </button>
      </DialogTrigger>
      <ModalContent closeLabel={t("close")} size="lg">
        <DialogTitle className="font-sans text-xl font-semibold">{t("sizeGuide")}</DialogTitle>
        <DialogDescription className="mb-4 text-sm text-muted">{t("sizeGuideHelp")}</DialogDescription>
        <div className="overflow-x-auto rounded-control border border-line">
          <table className="w-full text-sm">
            <thead className="bg-sunken text-start">
              <tr>
                {table.columns.map((column, i) => (
                  <th key={i} scope="col" className="whitespace-nowrap px-3 py-2 text-start font-semibold">
                    {column}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {table.rows.map((row, r) => (
                <tr key={r} className="border-t border-line">
                  {table.columns.map((_, c) => (
                    <td key={c} className={cn("whitespace-nowrap px-3 py-2", c === 0 && "font-medium")}>
                      {row[c] ?? ""}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </ModalContent>
    </Dialog>
  );
}

function Details({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <details className="group border-b border-line" open>
      <summary className="flex min-h-12 cursor-pointer list-none items-center justify-between font-semibold text-strong [&::-webkit-details-marker]:hidden">
        {title}
        <Plus className="size-4 group-open:hidden" aria-hidden />
        <Minus className="hidden size-4 group-open:block" aria-hidden />
      </summary>
      <div className="pb-4 text-sm leading-relaxed text-muted [overflow-wrap:anywhere]">{children}</div>
    </details>
  );
}

/**
 * The product page (SRS 3.4): pictures, name and price, colour and size pickers (sold-out sizes crossed out), quantity, Add to
 * cart, wishlist, size guide, delivery estimate, details, sharing, related and recently viewed products.
 */
export function ProductView({ slug, initial }: { slug: string; initial: StoreProductDetail }) {
  const t = useTranslations("store.product");
  const b = useTranslations("store.breadcrumb");
  const money = useMoney();
  const pick = usePickName();
  const router = useRouter();
  const storeError = useStoreError();
  const addToCart = useAddToCart();

  const live = useStoreProduct(slug);
  const product = live.data ?? initial;
  const card = product.card;
  const name = pick(card.nameEn, card.nameBn);
  useRememberProduct(card);

  const variants = product.variants;
  const colours = React.useMemo(() => {
    const seen = new Map<string, string | null>();
    for (const v of variants) if (v.colorName && !seen.has(v.colorName)) seen.set(v.colorName, v.colorHex);
    return [...seen].map(([colorName, colorHex]) => ({ colorName, colorHex }));
  }, [variants]);
  const sizes = React.useMemo(() => [...new Set(variants.map((v) => v.size).filter((s): s is string => !!s))], [variants]);

  const firstAvailable = variants.find((v) => v.inStock) ?? variants[0];
  const [colour, setColour] = React.useState<string | null>(firstAvailable?.colorName ?? null);
  const [size, setSize] = React.useState<string | null>(sizes.length === 1 ? sizes[0] : null);
  const [quantity, setQuantity] = React.useState(1);
  const [imageIndex, setImageIndex] = React.useState(0);
  const [needSize, setNeedSize] = React.useState(false);

  const find = (c: string | null, s: string | null): StoreVariant | undefined =>
    variants.find((v) => (colours.length === 0 || v.colorName === c) && (sizes.length === 0 || v.size === s));
  const variant = sizes.length === 0 || size ? find(colour, size) : undefined;
  const sizeSoldOut = (s: string) => !(find(colour, s)?.inStock ?? false);

  const chooseColour = (next: string) => {
    setColour(next);
    const index = product.images.findIndex((image) => image.colorName === next);
    if (index >= 0) setImageIndex(index);
    if (size && !(variants.find((v) => v.colorName === next && v.size === size)?.inStock ?? false)) setSize(null);
    setQuantity(1);
  };

  const unitPrice = variant ? toNumber(variant.price) : toNumber(card.price);
  const regular = toNumber(card.regularPrice);
  const onSale = unitPrice < regular;
  const percent = onSale ? Math.round(((regular - unitPrice) / regular) * 100) : 0;
  const stock = variant ? Math.min(toNumber(variant.available), MAX_QUANTITY) : MAX_QUANTITY;

  const add = async () => {
    if (sizes.length > 0 && !size) {
      setNeedSize(true);
      return;
    }
    if (!variant || !variant.inStock) return;
    try {
      await addToCart.mutateAsync({ variantId: variant.id, quantity });
      toast.success(t("added", { name }), { action: { label: t("viewCart"), onClick: () => router.push("/cart") } });
    } catch (error) {
      toast.error(storeError(error));
    }
  };

  const sizeTable = parseSizeTable(product.sizeChartJson);
  const tagList = (product.tags ?? "").split(",").map((x) => x.trim()).filter(Boolean);
  const soldOut = variants.length > 0 && variants.every((v) => !v.inStock);
  const crumbs = [
    { label: b("home"), href: "/" },
    ...product.breadcrumb.map((c) => ({ label: c.name, href: `/category/${c.slug}` })),
    { label: name },
  ];

  return (
    <>
      <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6">
        <Breadcrumbs label={b("label")} items={crumbs} />
        <div className="grid gap-8 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)] lg:gap-12">
          <ProductGallery images={product.images} name={name} index={imageIndex} onIndexChange={setImageIndex} />

          <div className="space-y-5">
            <div className="space-y-2">
              <div className="flex flex-wrap gap-1.5">
                {card.isNew ? <Badge variant="neutral">{t("new")}</Badge> : null}
                {onSale ? <Badge variant="sale">{t("save", { percent })}</Badge> : null}
              </div>
              <div className="flex items-start justify-between gap-3">
                <h1 className="text-3xl font-semibold leading-tight [overflow-wrap:anywhere]" data-testid="product-name">
                  {name}
                </h1>
                <WishlistHeart productId={card.id} name={name} className="border border-line" />
              </div>
              {card.tagline ? <p className="text-muted">{card.tagline}</p> : null}
              {card.brand ? <p className="text-sm text-muted">{t("brand", { brand: card.brand })}</p> : null}
              <p className="flex flex-wrap items-baseline gap-x-3" data-testid="product-price">
                <span className="text-3xl font-semibold text-strong">{money(unitPrice)}</span>
                {onSale ? (
                  <span className="text-lg text-muted line-through">
                    <span className="sr-only">{t("wasPrice")} </span>
                    {money(regular)}
                  </span>
                ) : null}
              </p>
            </div>

            {colours.length > 0 ? (
              <fieldset>
                <legend className="mb-2 text-sm font-semibold text-strong">
                  {t("colour")}: <span className="font-normal text-muted">{colour}</span>
                </legend>
                <div className="flex flex-wrap gap-2">
                  {colours.map((c) => {
                    const on = colour === c.colorName;
                    const out = !variants.some((v) => v.colorName === c.colorName && v.inStock);
                    return (
                      <button
                        key={c.colorName}
                        type="button"
                        aria-pressed={on}
                        aria-label={out ? t("colourSoldOut", { colour: c.colorName }) : c.colorName}
                        title={c.colorName}
                        onClick={() => chooseColour(c.colorName)}
                        className={cn("tap relative flex size-12 items-center justify-center rounded-full border-2", on ? "border-primary" : "border-transparent hover:border-line", out && "opacity-50")}
                      >
                        <span className="block size-8 rounded-full border border-line" style={{ backgroundColor: c.colorHex ?? "var(--surface-sunken)" }} />
                        {on ? <Check className="absolute size-4 text-white mix-blend-difference" aria-hidden /> : null}
                      </button>
                    );
                  })}
                </div>
              </fieldset>
            ) : null}

            {sizes.length > 0 ? (
              <fieldset>
                <div className="mb-2 flex items-center justify-between gap-3">
                  <legend className="text-sm font-semibold text-strong">
                    {t("size")}
                    {size ? <span className="font-normal text-muted">: {size}</span> : null}
                  </legend>
                  {sizeTable ? <SizeGuide table={sizeTable} /> : null}
                </div>
                <div className="flex flex-wrap gap-2" role="group" aria-label={t("size")}>
                  {sizes.map((s) => {
                    const out = sizeSoldOut(s);
                    const on = size === s;
                    return (
                      <button
                        key={s}
                        type="button"
                        aria-pressed={on}
                        aria-disabled={out}
                        onClick={() => {
                          if (out) return;
                          setSize(s);
                          setNeedSize(false);
                          setQuantity(1);
                        }}
                        className={cn(
                          "relative min-h-12 min-w-12 rounded-control border px-4 font-medium",
                          on ? "border-primary bg-primary-soft text-primary-dark" : "border-line text-strong hover:bg-sunken",
                          out && "cursor-not-allowed overflow-hidden text-muted line-through opacity-60 hover:bg-transparent",
                        )}
                      >
                        {s}
                        {out ? <span className="sr-only"> ({t("soldOut")})</span> : null}
                      </button>
                    );
                  })}
                </div>
                {needSize ? (
                  <p role="alert" className="mt-2 text-sm font-medium text-danger-ink">
                    {t("chooseSize")}
                  </p>
                ) : null}
              </fieldset>
            ) : null}

            <p className="min-h-6 text-sm" aria-live="polite" data-testid="availability">
              {soldOut ? (
                <span className="font-medium text-danger-ink">{t("soldOut")}</span>
              ) : variant ? (
                variant.inStock ? (
                  variant.lowStock ? (
                    <span className="font-medium text-warning-ink">{t("lowStock", { count: stock })}</span>
                  ) : (
                    <span className="font-medium text-success-ink">{t("inStock")}</span>
                  )
                ) : (
                  <span className="font-medium text-danger-ink">{t("soldOutOption")}</span>
                )
              ) : null}
            </p>

            <div className="flex flex-wrap items-center gap-3">
              <div className="inline-flex items-center rounded-control border border-line" role="group" aria-label={t("quantity")}>
                <button type="button" className="tap flex items-center justify-center disabled:opacity-40" aria-label={t("decrease")} disabled={quantity <= 1} onClick={() => setQuantity((q) => Math.max(1, q - 1))}>
                  <Minus className="size-4" aria-hidden />
                </button>
                <output className="min-w-10 text-center font-semibold" aria-live="polite" data-testid="quantity">
                  {quantity}
                </output>
                <button type="button" className="tap flex items-center justify-center disabled:opacity-40" aria-label={t("increase")} disabled={quantity >= stock} onClick={() => setQuantity((q) => Math.min(stock, q + 1))}>
                  <Plus className="size-4" aria-hidden />
                </button>
              </div>
              <Button size="lg" className="min-w-0 flex-1" onClick={() => void add()} loading={addToCart.isPending} disabled={soldOut || (!!variant && !variant.inStock)} data-testid="add-to-cart">
                <ShoppingBag aria-hidden /> {soldOut ? t("soldOut") : t("addToCart")}
              </Button>
            </div>
            {stock >= MAX_QUANTITY && variant ? <p className="-mt-2 text-xs text-muted">{t("maxPerOrder", { count: MAX_QUANTITY })}</p> : null}

            <ProductDelivery variantId={variant?.id ?? firstAvailable?.id} price={unitPrice * quantity} weightGrams={toNumber(product.weightGrams) * quantity} />

            <div>
              {product.description ? <Details title={t("description")}>{product.description.split(/\n{2,}/).map((p, i) => <p key={i} className="mb-2 whitespace-pre-line">{p}</p>)}</Details> : null}
              {product.material ? <Details title={t("material")}>{product.material}</Details> : null}
              {product.care ? <Details title={t("care")}>{product.care}</Details> : null}
              {tagList.length > 0 ? (
                <Details title={t("tags")}>
                  <ul className="flex flex-wrap gap-2">
                    {tagList.map((tag) => (
                      <li key={tag} className="rounded-full bg-sunken px-3 py-1 text-xs">
                        {tag}
                      </li>
                    ))}
                  </ul>
                </Details>
              ) : null}
            </div>

            <Share name={name} />
          </div>
        </div>
      </div>

      {product.related.length > 0 ? (
        <section className="mx-auto max-w-7xl px-4 py-8 sm:px-6" aria-labelledby="related-heading">
          <h2 id="related-heading" className="mb-5 text-2xl font-semibold">
            {t("related")}
          </h2>
          <ProductGrid products={product.related.slice(0, 4)} />
        </section>
      ) : null}
      <RecentlyViewed currentSlug={card.slug} />
      {live.isError && !live.data ? <Alert variant="warning" className="mx-auto mb-6 max-w-7xl">{t("stale")}</Alert> : null}
    </>
  );
}
