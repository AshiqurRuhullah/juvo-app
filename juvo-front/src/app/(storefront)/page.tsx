import Image from "next/image";
import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { ArrowRight, Banknote, RotateCcw, Truck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { storeGet } from "@/lib/server/store";
import { mediaUrl } from "@/lib/media";
import type { StoreHome, StoreProductCard } from "@/modules/store/api";
import { ProductGrid } from "@/modules/store/store-product-card";

function Section({ id, title, href, linkLabel, products }: { id: string; title: string; href: string; linkLabel: string; products: StoreProductCard[] }) {
  if (products.length === 0) return null;
  return (
    <section className="mx-auto max-w-7xl px-4 py-8 sm:px-6" aria-labelledby={id}>
      <div className="mb-5 flex items-end justify-between gap-4">
        <h2 id={id} className="text-2xl font-semibold sm:text-3xl">
          {title}
        </h2>
        <Link href={href} className="inline-flex min-h-11 items-center gap-1 text-sm font-semibold text-primary hover:underline">
          {linkLabel} <ArrowRight className="size-4 rtl:rotate-180" aria-hidden />
        </Link>
      </div>
      <ProductGrid products={products.slice(0, 8)} />
    </section>
  );
}

/** Storefront home: hero, categories, then the shelves the API prepares (new arrivals, best sellers, sale, featured). */
export default async function StorefrontHome() {
  const t = await getTranslations("store.home");
  const locale = await getLocale();
  const home = await storeGet<StoreHome>("/home");
  const categories = home?.categories ?? [];

  const trust = [
    { icon: Banknote, title: t("trustCodTitle"), body: t("trustCodBody") },
    { icon: Truck, title: t("trustDeliveryTitle"), body: t("trustDeliveryBody") },
    { icon: RotateCcw, title: t("trustReturnsTitle"), body: t("trustReturnsBody") },
  ];

  return (
    <>
      <section className="relative isolate overflow-hidden bg-secondary text-accent" aria-labelledby="hero-heading">
        <div className="mx-auto grid max-w-7xl items-center gap-8 px-4 py-12 sm:px-6 md:grid-cols-2 md:py-20">
          <div className="space-y-5">
            <p className="text-sm font-semibold uppercase tracking-widest text-accent/80">{t("kicker")}</p>
            <h1 id="hero-heading" className="text-4xl font-semibold leading-tight text-on-primary sm:text-5xl">
              {t("heroTitle")}
            </h1>
            <p className="max-w-lg text-lg text-accent/90">{t("heroBody")}</p>
            <div className="flex flex-wrap gap-3 pt-2">
              <Button asChild size="lg">
                <Link href="/shop">{t("shopNow")}</Link>
              </Button>
              <Button asChild size="lg" variant="secondary" className="border-accent/40 bg-transparent text-accent [@media(hover:hover)]:hover:bg-accent/10">
                <Link href="/shop?onSale=1">{t("seeSale")}</Link>
              </Button>
            </div>
          </div>
          <div className="mx-auto hidden w-full max-w-xs md:block">
            <Image src="/brand/logo-original.webp" alt="Juvo BD" width={512} height={512} priority className="aspect-square w-full rounded-card object-cover shadow-pop" />
          </div>
        </div>
      </section>

      {categories.length > 0 ? (
        <section className="mx-auto max-w-7xl px-4 pt-10 sm:px-6" aria-labelledby="cat-heading">
          <h2 id="cat-heading" className="mb-5 text-2xl font-semibold sm:text-3xl">
            {t("shopByCategory")}
          </h2>
          <ul className="grid grid-cols-2 gap-3 sm:gap-4 md:grid-cols-4">
            {categories.slice(0, 8).map((category) => {
              const image = mediaUrl(category.imageUrl);
              const name = locale === "bn" && category.nameBn ? category.nameBn : category.nameEn;
              return (
                <li key={category.id}>
                  <Link href={`/category/${category.slug}`} className="group relative block aspect-[4/3] overflow-hidden rounded-card border border-line bg-sunken">
                    {image ? <Image src={image} alt="" fill unoptimized sizes="(min-width: 768px) 25vw, 50vw" className="object-cover transition-transform duration-300 [@media(hover:hover)]:group-hover:scale-105" /> : null}
                    <span className="absolute inset-x-0 bottom-0 flex min-h-11 flex-col justify-end bg-gradient-to-t from-black/65 to-transparent p-3 text-white">
                      <span className="font-semibold leading-tight">{name}</span>
                      <span className="text-xs text-white/85">{t("productCount", { count: Number(category.productCount) })}</span>
                    </span>
                  </Link>
                </li>
              );
            })}
          </ul>
        </section>
      ) : null}

      {home ? (
        <>
          <Section id="new" title={t("newArrivals")} href="/shop" linkLabel={t("viewAll")} products={home.newArrivals} />
          <Section id="best" title={t("bestSellers")} href="/shop?sort=popular" linkLabel={t("viewAll")} products={home.bestSellers} />
          <Section id="sale" title={t("onSale")} href="/shop?onSale=1" linkLabel={t("viewAll")} products={home.onSale} />
          <Section id="featured" title={t("featured")} href="/shop" linkLabel={t("viewAll")} products={home.featured} />
        </>
      ) : (
        <section className="mx-auto max-w-7xl px-4 py-12 text-center sm:px-6">
          <p className="text-muted">{t("unavailable")}</p>
          <Button asChild className="mt-4">
            <Link href="/shop">{t("shopNow")}</Link>
          </Button>
        </section>
      )}

      <section className="mx-auto max-w-7xl px-4 py-10 sm:px-6" aria-labelledby="trust-heading">
        <h2 id="trust-heading" className="sr-only">
          {t("trustHeading")}
        </h2>
        <ul className="grid gap-4 sm:grid-cols-3">
          {trust.map(({ icon: Icon, title, body }) => (
            <li key={title} className="flex items-start gap-4 rounded-card border border-line bg-card p-5">
              <span className="flex size-11 shrink-0 items-center justify-center rounded-full bg-primary-soft text-primary-dark">
                <Icon className="size-5" aria-hidden />
              </span>
              <div>
                <h3 className="font-sans text-base font-semibold">{title}</h3>
                <p className="text-sm text-muted">{body}</p>
              </div>
            </li>
          ))}
        </ul>
        <p className="mt-8 text-center text-sm text-muted">
          {t("brandLine")}{" "}
          <a className="font-semibold text-primary hover:underline" href="https://facebook.com/juvobd" target="_blank" rel="noopener noreferrer">
            facebook.com/juvobd
          </a>
        </p>
      </section>
    </>
  );
}
