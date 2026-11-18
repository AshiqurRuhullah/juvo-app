import Image from "next/image";
import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { ArrowRight } from "lucide-react";
import { storeGet } from "@/lib/server/store";
import { mediaUrl } from "@/lib/media";
import type { Schemas } from "@/lib/api/types";
import { PromotionCountdown } from "./promo-parts";

export type StoreBanner = Schemas["StoreBannerDto"];
export type BannerSlot = "HomeTop" | "ShopTop" | "Category";

/**
 * The banners the API says are running now for one slot (home top, shop top, a category). Nothing is drawn when there are none, and
 * nothing is invented. Read fresh every time: the API cuts its own cache at the next promotion start or end and this page must not
 * keep an answer past that (decision P12).
 */
export async function PromoBanners({ slot, category }: { slot: BannerSlot; category?: string }) {
  const banners = await storeGet<StoreBanner[]>("/banners", { slot, category }, { fresh: true });
  if (!banners || banners.length === 0) return null;
  const t = await getTranslations("storepromo.banners");
  const locale = await getLocale();
  return (
    <section className="mx-auto max-w-7xl px-4 pt-6 sm:px-6" aria-label={t("label")} data-testid="promo-banners" data-slot={slot}>
      <ul className={banners.length > 1 ? "grid gap-4 md:grid-cols-2" : "grid gap-4"}>
        {banners.map((banner) => {
          const title = (locale === "bn" ? banner.titleBn || banner.titleEn : banner.titleEn || banner.titleBn) || (locale === "bn" ? banner.labelBn || banner.labelEn : banner.labelEn || banner.labelBn);
          const label = locale === "bn" ? banner.labelBn || banner.labelEn : banner.labelEn || banner.labelBn;
          const image = mediaUrl(banner.imageUrl);
          const inner = (
            <>
              {image ? <Image src={image} alt="" fill unoptimized sizes="(min-width: 768px) 50vw, 100vw" className="object-cover" /> : null}
              <span className="absolute inset-x-0 bottom-0 flex flex-col gap-1 bg-gradient-to-t from-black/75 via-black/40 to-transparent p-4 pt-12 text-white sm:p-5 sm:pt-16">
                <span className="text-xs font-semibold uppercase tracking-widest text-white/85">{label}</span>
                <span className="text-xl font-semibold leading-tight sm:text-2xl">{title}</span>
                {banner.slug ? (
                  <span className="mt-1 inline-flex min-h-6 items-center gap-1 text-sm font-semibold">
                    {t("cta")} <ArrowRight className="size-4 rtl:rotate-180" aria-hidden />
                  </span>
                ) : null}
              </span>
            </>
          );
          return (
            <li key={banner.promotionId} className="space-y-2" data-testid="promo-banner">
              {banner.slug ? (
                <Link href={`/campaign/${banner.slug}`} className="group relative block aspect-[16/7] overflow-hidden rounded-card border border-line bg-sunken focus-visible:outline-offset-2">
                  {inner}
                </Link>
              ) : (
                <div className="relative block aspect-[16/7] overflow-hidden rounded-card border border-line bg-sunken">{inner}</div>
              )}
              {banner.endsAt ? <PromotionCountdown promotion={{ endsAt: banner.endsAt }} /> : null}
            </li>
          );
        })}
      </ul>
    </section>
  );
}
