import type { Metadata } from "next";
import Image from "next/image";
import Link from "next/link";
import { notFound } from "next/navigation";
import { getLocale, getTranslations } from "next-intl/server";
import { PackageSearch } from "lucide-react";
import { buttonVariants } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/surfaces";
import { mediaUrl } from "@/lib/media";
import { storeGet } from "@/lib/server/store";
import { PromotionCountdown } from "@/modules/promotions-store/promo-parts";
import type { StoreCampaign } from "@/modules/promotions-store/api";
import { Breadcrumbs } from "@/modules/store/page-heading";
import { ProductGrid } from "@/modules/store/store-product-card";

type Params = { slug: string };

// Read fresh: a campaign page exists only while the promotion runs, and the API's cache is cut at its start and end (decision P12).
const load = (slug: string) => storeGet<StoreCampaign>(`/campaigns/${encodeURIComponent(decodeURIComponent(slug))}`, undefined, { fresh: true });

const text = (locale: string, en: string | null | undefined, bn: string | null | undefined) => (locale === "bn" ? bn || en : en || bn) || "";

export async function generateMetadata({ params }: { params: Promise<Params> }): Promise<Metadata> {
  const campaign = await load((await params).slug);
  const t = await getTranslations("storepromo.campaign");
  if (!campaign) return { title: t("notFoundTitle") };
  const locale = await getLocale();
  const title = text(locale, campaign.titleEn, campaign.titleBn) || text(locale, campaign.labelEn, campaign.labelBn);
  return { title, description: text(locale, campaign.bodyEn, campaign.bodyBn).slice(0, 160) || undefined };
}

/** A festival campaign page (FR-Z03): hero, the story, a countdown when the campaign has an end, and its products in the shop grid. */
export default async function CampaignPage({ params }: { params: Promise<Params> }) {
  const { slug } = await params;
  const campaign = await load(slug);
  if (!campaign) notFound();
  const locale = await getLocale();
  const t = await getTranslations("storepromo.campaign");
  const b = await getTranslations("store.breadcrumb");
  const title = text(locale, campaign.titleEn, campaign.titleBn) || text(locale, campaign.labelEn, campaign.labelBn);
  const body = text(locale, campaign.bodyEn, campaign.bodyBn);
  const hero = mediaUrl(campaign.heroImageUrl) ?? mediaUrl(campaign.bannerImageUrl);
  const products = campaign.products ?? [];

  return (
    <div className="mx-auto max-w-7xl px-4 py-6 sm:px-6">
      <Breadcrumbs label={b("label")} items={[{ label: b("home"), href: "/" }, { label: title }]} />
      <section className="relative isolate mb-8 overflow-hidden rounded-card border border-line bg-black" aria-labelledby="campaign-title" data-testid="campaign-hero">
        {hero ? <Image src={hero} alt="" fill unoptimized priority sizes="100vw" className="-z-10 object-cover opacity-60" /> : null}
        <div className="flex min-h-56 flex-col justify-end gap-3 bg-gradient-to-t from-black/70 via-black/30 to-transparent p-5 sm:min-h-72 sm:p-8">
          <p className="text-sm font-semibold uppercase tracking-widest text-white/85">{text(locale, campaign.labelEn, campaign.labelBn)}</p>
          <h1 id="campaign-title" className="max-w-3xl text-3xl font-semibold leading-tight text-white sm:text-5xl" data-testid="campaign-title">
            {title}
          </h1>
          {body ? (
            <div className="max-w-2xl space-y-2 text-base text-white/90 sm:text-lg" data-testid="campaign-body">
              {body.split(/\n{2,}/).map((paragraph, index) => (
                <p key={index} className="whitespace-pre-line [overflow-wrap:anywhere]">
                  {paragraph}
                </p>
              ))}
            </div>
          ) : null}
          {campaign.endsAt ? <PromotionCountdown promotion={{ endsAt: campaign.endsAt }} size="lg" className="self-start" /> : null}
        </div>
      </section>

      <h2 className="mb-5 text-2xl font-semibold">{t("products")}</h2>
      {products.length > 0 ? (
        <ProductGrid products={products} priorityCount={4} />
      ) : (
        <EmptyState
          icon={<PackageSearch aria-hidden />}
          title={t("emptyTitle")}
          description={t("emptyText")}
          action={
            <Link href="/shop" className={buttonVariants({ size: "lg" })}>
              {t("shop")}
            </Link>
          }
        />
      )}
    </div>
  );
}
