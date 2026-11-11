"use client";

import { useQuery } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";

export type QuoteTarget = { countryCode?: string; districtId?: string; upazilaId?: string };

/**
 * The delivery charge for what is in the cart, to a chosen place. It is a query (not a mutation) keyed by the place and by the cart's
 * total and weight, so it refreshes by itself when the cart changes (every cart change also invalidates the whole ["store","cart"] key).
 */
export function useCartQuote(target: QuoteTarget, cartKey: string) {
  const enabled = !!(target.districtId || target.countryCode);
  return useQuery({
    queryKey: ["store", "cart", "quote", target, cartKey],
    enabled,
    retry: false,
    queryFn: async () =>
      unwrap(
        await api.POST("/api/v1/store/cart/delivery-quote", {
          body: { countryCode: target.countryCode ?? null, districtId: target.districtId ?? null, upazilaId: target.upazilaId ?? null },
        }),
      ),
  });
}

const STORAGE_KEY = "juvo_delivery_to";

export type SavedPlace = { mode: "bd" | "abroad"; divisionId?: string; districtId?: string; upazilaId?: string; countryCode?: string };

/** The delivery place the visitor chose last time (browser storage may be blocked: then nothing is remembered). */
export function readSavedPlace(): SavedPlace | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw ? (JSON.parse(raw) as SavedPlace) : null;
  } catch {
    return null;
  }
}

export function writeSavedPlace(place: SavedPlace) {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(place));
  } catch {
    // Blocked storage only means the choice is not remembered.
  }
}
