"use client";

import * as React from "react";
import Image from "next/image";
import { useTranslations } from "next-intl";
import { ChevronLeft, ChevronRight, ImageOff, Maximize2 } from "lucide-react";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { cn } from "@/lib/utils";
import { mediaUrl } from "@/modules/catalog/format";
import type { StoreImage } from "./api";

/**
 * The product pictures: a swipeable strip on phones (scroll-snap), arrows and thumbnails on larger screens, and a full-size
 * lightbox on tap or click. `index` is controlled so choosing a colour can jump to that colour's first picture.
 */
export function ProductGallery({ images, name, index, onIndexChange }: { images: StoreImage[]; name: string; index: number; onIndexChange: (index: number) => void }) {
  const t = useTranslations("store.gallery");
  const strip = React.useRef<HTMLDivElement>(null);
  const [lightbox, setLightbox] = React.useState(false);

  const count = images.length;
  const go = React.useCallback(
    (next: number) => {
      const target = (next + count) % count;
      onIndexChange(target);
      const node = strip.current;
      if (node) node.scrollTo({ left: target * node.clientWidth * (getComputedStyle(node).direction === "rtl" ? -1 : 1), behavior: "smooth" });
    },
    [count, onIndexChange],
  );

  // When the index is changed from outside (a colour was chosen), bring that picture into view.
  React.useEffect(() => {
    const node = strip.current;
    if (!node || node.clientWidth === 0) return;
    const dir = getComputedStyle(node).direction === "rtl" ? -1 : 1;
    if (Math.round(Math.abs(node.scrollLeft) / node.clientWidth) !== index) node.scrollTo({ left: index * node.clientWidth * dir, behavior: "smooth" });
  }, [index]);

  const onScroll = () => {
    const node = strip.current;
    if (!node || node.clientWidth === 0) return;
    const current = Math.round(Math.abs(node.scrollLeft) / node.clientWidth);
    if (current !== index && current < count) onIndexChange(current);
  };

  if (count === 0) {
    return (
      <div className="flex aspect-[4/5] w-full flex-col items-center justify-center gap-2 rounded-card border border-line bg-sunken text-muted">
        <ImageOff className="size-10" aria-hidden />
        <span>{t("none")}</span>
      </div>
    );
  }

  const current = images[Math.min(index, count - 1)];

  return (
    <div className="space-y-3 lg:grid lg:grid-cols-[5rem_1fr] lg:gap-3 lg:space-y-0">
      {count > 1 ? (
        <ul className="order-2 hidden gap-2 lg:order-1 lg:flex lg:flex-col" aria-label={t("thumbnails")}>
          {images.map((image, i) => (
            <li key={image.id}>
              <button
                type="button"
                onClick={() => go(i)}
                aria-label={t("show", { n: i + 1, total: count })}
                aria-current={i === index}
                className={cn("relative block aspect-[4/5] w-full overflow-hidden rounded-md border-2 bg-sunken", i === index ? "border-primary" : "border-transparent hover:border-line")}
              >
                <Image src={mediaUrl(image.thumbUrl) ?? ""} alt="" fill unoptimized sizes="80px" className="object-cover" />
              </button>
            </li>
          ))}
        </ul>
      ) : null}

      <div className="relative order-1 min-w-0 lg:order-2">
        <div ref={strip} onScroll={onScroll} className="flex snap-x snap-mandatory overflow-x-auto overscroll-x-contain rounded-card border border-line bg-sunken [scrollbar-width:none] [&::-webkit-scrollbar]:hidden" data-testid="gallery-strip">
          {images.map((image, i) => (
            <button key={image.id} type="button" onClick={() => setLightbox(true)} aria-label={t("enlarge")} className="relative aspect-[4/5] w-full shrink-0 snap-center cursor-zoom-in">
              <Image
                src={mediaUrl(image.cardUrl ?? image.url) ?? ""}
                alt={image.altText || (count > 1 ? t("alt", { name, n: i + 1 }) : name)}
                fill
                unoptimized
                priority={i === 0}
                sizes="(min-width: 1024px) 45vw, 100vw"
                placeholder={image.blurDataUrl ? "blur" : "empty"}
                blurDataURL={image.blurDataUrl ?? undefined}
                className="object-cover"
              />
            </button>
          ))}
        </div>
        <span className="pointer-events-none absolute end-3 top-3 flex size-9 items-center justify-center rounded-full bg-card/90 text-strong shadow-sm" aria-hidden>
          <Maximize2 className="size-4" />
        </span>
        {count > 1 ? (
          <>
            <button type="button" onClick={() => go(index - 1)} aria-label={t("previous")} className="tap absolute start-2 top-1/2 hidden size-11 -translate-y-1/2 items-center justify-center rounded-full bg-card/90 shadow-sm hover:bg-card md:flex">
              <ChevronLeft className="size-5 rtl:rotate-180" aria-hidden />
            </button>
            <button type="button" onClick={() => go(index + 1)} aria-label={t("next")} className="tap absolute end-2 top-1/2 hidden size-11 -translate-y-1/2 items-center justify-center rounded-full bg-card/90 shadow-sm hover:bg-card md:flex">
              <ChevronRight className="size-5 rtl:rotate-180" aria-hidden />
            </button>
            <p className="absolute bottom-3 start-1/2 -translate-x-1/2 rounded-full bg-card/90 px-3 py-1 text-xs font-medium text-strong shadow-sm rtl:translate-x-1/2 lg:hidden" aria-live="polite">
              {index + 1} / {count}
            </p>
          </>
        ) : null}
      </div>

      <Dialog open={lightbox} onOpenChange={setLightbox}>
        {lightbox ? (
          <ModalContent closeLabel={t("close")} size="lg" className="items-center gap-3 md:p-4">
            <DialogTitle className="sr-only">{name}</DialogTitle>
            <DialogDescription className="sr-only">{t("lightboxHelp")}</DialogDescription>
            <div className="relative aspect-[4/5] w-full max-w-xl overflow-hidden rounded-card bg-sunken">
              <Image src={mediaUrl(current.url) ?? ""} alt={current.altText || name} fill unoptimized sizes="(min-width: 768px) 576px, 100vw" className="object-contain" />
            </div>
            {count > 1 ? (
              <div className="flex items-center gap-3">
                <button type="button" onClick={() => go(index - 1)} aria-label={t("previous")} className="tap flex items-center justify-center rounded-full border border-line">
                  <ChevronLeft className="size-5 rtl:rotate-180" aria-hidden />
                </button>
                <span className="text-sm text-muted">{index + 1} / {count}</span>
                <button type="button" onClick={() => go(index + 1)} aria-label={t("next")} className="tap flex items-center justify-center rounded-full border border-line">
                  <ChevronRight className="size-5 rtl:rotate-180" aria-hidden />
                </button>
              </div>
            ) : null}
          </ModalContent>
        ) : null}
      </Dialog>
    </div>
  );
}
