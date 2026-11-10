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
