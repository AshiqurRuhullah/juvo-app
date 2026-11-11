import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getLocale } from "next-intl/server";
import { storeGet } from "@/lib/server/store";
import type { StoreProductDetail } from "@/modules/store/api";
import { ProductView } from "@/modules/store/product-view";

type Params = { slug: string };

const load = (slug: string) => storeGet<StoreProductDetail>(`/products/${encodeURIComponent(decodeURIComponent(slug))}`);

const absolute = (path: string | null | undefined) => (path ? (path.startsWith("/files/") ? `/media/${path.slice("/files/".length)}` : path) : undefined);

export async function generateMetadata({ params }: { params: Promise<Params> }): Promise<Metadata> {
  const product = await load((await params).slug);
  if (!product) return { title: "Product not found" };
  const locale = await getLocale();
  const card = product.card;
  const name = locale === "bn" && card.nameBn ? card.nameBn : card.nameEn;
  const description = product.metaDescription || card.tagline || product.description?.slice(0, 160) || undefined;
  const image = absolute(product.images[0]?.url ?? card.imageUrl);
  return {
    title: product.metaTitle || name,
    description,
    openGraph: { title: product.metaTitle || name, description, type: "website", images: image ? [{ url: image }] : undefined },
    twitter: { card: "summary_large_image", title: product.metaTitle || name, description },
  };
}

/** One product: the server fetches it for the first paint, structured data for search engines, and a friendly 404 when it does not exist. */
export default async function ProductPage({ params }: { params: Promise<Params> }) {
  const { slug } = await params;
  const product = await load(slug);
  if (!product) notFound();
  const card = product.card;

  const jsonLd = {
    "@context": "https://schema.org",
    "@type": "Product",
    name: card.nameEn,
    description: product.description ?? card.tagline ?? undefined,
    sku: product.variants[0]?.sku,
    image: product.images.map((i) => absolute(i.url)).filter(Boolean),
    brand: card.brand ? { "@type": "Brand", name: card.brand } : undefined,
    offers: {
      "@type": "Offer",
      priceCurrency: "BDT",
      price: Number(card.price),
      availability: product.variants.some((v) => v.inStock) ? "https://schema.org/InStock" : "https://schema.org/OutOfStock",
    },
  };

  return (
    <>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd).replace(/</g, "\\u003c") }} />
      <ProductView slug={card.slug} initial={product} />
    </>
  );
}
