import "server-only";
import { apiBaseUrl } from "./backend";

type Query = Record<string, string | number | boolean | undefined | null>;

/**
 * Reads the public shop API (`/api/v1/store/...`) on the server for the first paint of storefront pages (SEO, no loading flash).
 * The answers are public, so they are cached for a minute. Returns null when the API says "not found" or cannot be reached; the
 * page then shows its friendly empty or 404 state. The browser takes over with TanStack Query for everything interactive.
 */
export async function storeGet<T>(path: string, query?: Query): Promise<T | null> {
  const url = new URL(`${apiBaseUrl()}/api/v1/store${path}`);
  for (const [key, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== null && value !== "" && value !== false) url.searchParams.set(key, String(value));
  }
  try {
    const response = await fetch(url, { headers: { Accept: "application/json" }, next: { revalidate: 60 } });
    if (!response.ok) return null;
    return (await response.json()) as T;
  } catch {
    return null;
  }
}
