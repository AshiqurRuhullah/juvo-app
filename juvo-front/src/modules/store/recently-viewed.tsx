"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { StoreProductCard } from "./api";
import { ProductGrid } from "./store-product-card";

const KEY = "juvo_recently_viewed";
const EVENT = "juvo:recently-viewed";
const MAX = 8;
const EMPTY: StoreProductCard[] = [];

let cachedRaw: string | null = null;
let cachedList: StoreProductCard[] = EMPTY;

function readRaw(): string | null {
  try {
    return window.localStorage.getItem(KEY);
  } catch {
    return null;
  }
}

/** The stored list; the same array object is returned while the stored text is unchanged (required by useSyncExternalStore). */
function snapshot(): StoreProductCard[] {
  const raw = readRaw();
  if (raw === cachedRaw) return cachedList;
  cachedRaw = raw;
  try {
    const parsed = raw ? (JSON.parse(raw) as unknown) : [];
    cachedList = Array.isArray(parsed) ? (parsed as StoreProductCard[]).filter((p) => p && typeof p.slug === "string") : EMPTY;
  } catch {
    cachedList = EMPTY;
  }
  return cachedList;
}

function subscribe(notify: () => void) {
  window.addEventListener(EVENT, notify);
  window.addEventListener("storage", notify);
  return () => {
    window.removeEventListener(EVENT, notify);
    window.removeEventListener("storage", notify);
  };
}

/** Remembers this product as the most recently viewed one (kept in the browser only; failures such as private mode are ignored). */
export function useRememberProduct(card: StoreProductCard) {
  React.useEffect(() => {
    try {
      const rest = snapshot().filter((p) => p.slug !== card.slug);
      window.localStorage.setItem(KEY, JSON.stringify([card, ...rest].slice(0, MAX)));
      window.dispatchEvent(new Event(EVENT));
    } catch {
      /* storage is unavailable: the section simply stays empty */
    }
  }, [card]);
}

/** "Recently viewed": the last few products this browser opened, except the current one. Shows nothing until there is something. */
export function RecentlyViewed({ currentSlug }: { currentSlug: string }) {
  const t = useTranslations("store.product");
  const all = React.useSyncExternalStore(subscribe, snapshot, () => EMPTY);
  const items = all.filter((p) => p.slug !== currentSlug).slice(0, 4);
  if (items.length === 0) return null;
  return (
    <section className="mx-auto max-w-7xl px-4 py-8 sm:px-6" aria-labelledby="recent-heading" data-testid="recently-viewed">
      <h2 id="recent-heading" className="mb-5 text-2xl font-semibold">
        {t("recentlyViewed")}
      </h2>
      <ProductGrid products={items} />
    </section>
  );
}
