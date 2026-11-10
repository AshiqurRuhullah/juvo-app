"use client";

import { useFormatter } from "next-intl";

/** The API sends some numbers as numbers and some as strings (JSON from .NET); this turns either into a number. */
export function toNumber(value: number | string | null | undefined): number {
  if (value === null || value === undefined || value === "") return 0;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
}

/** Money in Bangladeshi taka, in the current language ("৳1,250" / "৳১,২৫০"), without decimals when the amount is whole. */
export function useMoney() {
  const format = useFormatter();
  return (value: number | string | null | undefined) => {
    const n = toNumber(value);
    return format.number(n, { style: "currency", currency: "BDT", currencyDisplay: "narrowSymbol", maximumFractionDigits: Number.isInteger(n) ? 0 : 2 });
  };
}

/** The API gives picture addresses under /files; this app serves them from /media (same origin, cached). */
export function mediaUrl(apiUrl: string | null | undefined): string | undefined {
  if (!apiUrl) return undefined;
  return apiUrl.startsWith("/files/") ? `/media/${apiUrl.slice("/files/".length)}` : apiUrl;
}

/** Address of the card (800 x 1000) or thumbnail (160 x 200) rendition of a stored picture path such as products/2611/ab/cd.webp. */
export function renditionUrl(path: string | null | undefined, kind: "card" | "thumb" | "main" = "card"): string | undefined {
  if (!path) return undefined;
  const dot = path.lastIndexOf(".");
  const withSuffix = kind === "main" || dot <= path.lastIndexOf("/") ? path : `${path.slice(0, dot)}-${kind}${path.slice(dot)}`;
  return `/media/${withSuffix}`;
}

/** Percentage off, rounded, for the Sale badge. */
export function discountPercent(regular: number, effective: number): number {
  return regular > 0 && effective < regular ? Math.round(((regular - effective) / regular) * 100) : 0;
}

/**
 * Address of one rendition of a product picture. A new focal point re-cuts the card and thumbnail under the same file name, and the
 * files are cached for a year, so the focal point is part of the address (`?v=`): changing it gives a new address.
 */
export function imageSrc(image: { url: string; cardUrl: string; thumbUrl: string; focalX: number | string; focalY: number | string }, kind: "main" | "card" | "thumb" = "card"): string {
  const base = mediaUrl(kind === "main" ? image.url : kind === "card" ? image.cardUrl : image.thumbUrl) ?? "";
  return `${base}?v=${toNumber(image.focalX).toFixed(3)}-${toNumber(image.focalY).toFixed(3)}`;
}

/** The part of the picture the card shows, in 0..1 of the main picture (the same maths as the API's crop). */
export function cropWindow(width: number, height: number, focalX: number, focalY: number) {
  const cropW = Math.min(width, Math.floor((height * 4) / 5));
  const cropH = Math.min(height, Math.floor((cropW * 5) / 4));
  const left = Math.min(Math.max(Math.round(focalX * width - cropW / 2), 0), width - cropW);
  const top = Math.min(Math.max(Math.round(focalY * height - cropH / 2), 0), height - cropH);
  return { left: left / width, top: top / height, width: cropW / width, height: cropH / height };
}
