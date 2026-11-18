import "server-only";
import { apiBaseUrl } from "./backend";

type Query = Record<string, string | number | boolean | undefined | null>;

/**
 * Reads the public shop API (`/api/v1/store/...`) on the server for the first paint of storefront pages (SEO, no loading flash).
 * The answers are public, so they are cached for a minute. Returns null when the API says "not found" or cannot be reached; the
 * page then shows its friendly empty or 404 state. `fresh: true` skips this layer's one-minute cache (promotions, banners). The browser takes over with TanStack Query for everything interactive.
 */
export async function storeGet<T>(path: string, query?: Query, options: { fresh?: boolean } = {}): Promise<T | null> {
  const url = new URL(`${apiBaseUrl()}/api/v1/store${path}`);
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== null && value !== "" && value !== false) url.searchParams.set(key, String(value));
  }
  try {
    // `fresh`: pages that can show a promotion price or banner never reuse an answer longer than the API allows (decision P12). The API
    // cuts its own Cache-Control at the next promotion start or end, and this layer must not keep the answer past that, so it keeps none.
    const response = await fetch(url, { headers: { Accept: "application/json" }, ...(options.fresh ? { cache: "no-store" as const } : { next: { revalidate: 60 } }) });
    if (!response.ok) return null;
    return (await response.json()) as T;
  } catch {
    return null;
  }
}
