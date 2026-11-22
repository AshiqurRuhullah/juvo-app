import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { absoluteUrl, buildBreadcrumbJsonLd, buildProductJsonLd, jsonLdString } from "@/lib/seo";
import { storeGet } from "@/lib/server/store";
import type { StoreProductDetail } from "@/modules/store/api";
import { ProductView } from "@/modules/store/product-view";

type Params = { slug: string };

const load = (slug: string) => storeGet<StoreProductDetail>(`/products/${encodeURIComponent(decodeURIComponent(slug))}`, undefined, { fresh: true });

const FALLBACK_IMAGE = "/brand/og-image.png"; // the shop's own share picture, used when a product has no picture

const localName = (locale: string, card: StoreProductDetail["card"]) => (locale === "bn" && card.nameBn ? card.nameBn : card.nameEn);

export async function generateMetadata({ params }: { params: Promise<Params> }): Promise<Metadata> {
  const product = await load((await params).slug);
  if (!product) return { title: "Product not found", robots: { index: false } };
  const locale = await getLocale();
  const card = product.card;
  const name = localName(locale, card);
  const title = product.metaTitle || name;
  const description = product.metaDescription || card.tagline || product.description?.slice(0, 160) || undefined;
  const path = `/product/${encodeURIComponent(card.slug)}`;
  // Absolute address of the main picture through the same-origin /media route (Facebook and WhatsApp need a full URL).
  const image = absoluteUrl(product.images[0]?.url ?? card.imageUrl) ?? absoluteUrl(FALLBACK_IMAGE);
  return {
    title,
    description,
    alternates: { canonical: path },
    openGraph: { title, description, type: "website", url: path, images: image ? [{ url: image, alt: product.images[0]?.altText || name }] : undefined },
    twitter: { card: "summary_large_image", title, description, images: image ? [image] : undefined },
  };
}

/** One product: the server fetches it for the first paint, structured data for search engines, and a friendly 404 when it does not exist. */
export default async function ProductPage({ params }: { params: Promise<Params> }) {
  const { slug } = await params;
  const product = await load(slug);
  if (!product) notFound();
  const card = product.card;
  const locale = await getLocale();
  const name = localName(locale, card);
  const url = absoluteUrl(`/product/${encodeURIComponent(card.slug)}`) as string;

  const productLd = buildProductJsonLd({
    name,
    url,
    description: product.description ?? card.tagline,
    sku: product.variants[0]?.sku,
    images: product.images.map((i) => absoluteUrl(i.url)).filter((u): u is string => !!u),
    brand: card.brand,
    price: card.price,
    inStock: product.variants.some((v) => v.inStock),
    preorder: product.variants.some((v) => v.isPreorder),
    // The public API has no reviews yet, so no aggregateRating is written (decision in docs/seo-notes.md).
  });
  const home = await getTranslations("store.breadcrumb");
  const breadcrumbLd = buildBreadcrumbJsonLd([
    { name: home("home"), url: absoluteUrl("/") as string },
    ...product.breadcrumb.map((crumb) => ({ name: crumb.name, url: absoluteUrl(`/category/${encodeURIComponent(crumb.slug)}`) as string })),
    { name, url },
  ]);

  return (
    <>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: jsonLdString(productLd) }} />
      {breadcrumbLd ? <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: jsonLdString(breadcrumbLd) }} /> : null}
      <ProductView slug={card.slug} initial={product} />
    </>
  );
}
