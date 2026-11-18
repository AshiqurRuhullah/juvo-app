import { MESSAGE_FRAGMENTS, type MessageLocale } from "./load";

/**
 * Which texts each part of the site sends to the browser.
 *
 * Server components read the full catalogue on the server (getTranslations), but everything a client component needs travels inside
 * the page's HTML. Sending the whole catalogue (about 45 kB compressed) with every page made even the login page heavy. Each area now
 * gets only the namespaces its client components use. `tests/messages-scope.test.ts` follows the imports of every page and layout
 * and fails when a client component uses a namespace its area does not list, so a missing text cannot ship unnoticed.
 *
 * A namespace is the first part of a message key: `orders.status.Placed` belongs to `orders`. Namespaces are either top-level keys of
 * the base file (`errors`, `nav`, `ums`, ...) or one of MESSAGE_FRAGMENTS (`orders`, `store`, ...).
 */

/** Used on every page (root layout): errors and the small shared pieces. */
const ROOT = ["common", "errors", "errorPages", "table", "live"] as const;

export const SCOPES = {
  root: [...ROOT],
  /** Staff and customer sign-in, password and first-login pages. */
  auth: [...ROOT, "auth", "otp", "firstLogin", "twoFactor"],
  /** Shop, product pages, cart, checkout, payment pages, order tracking. */
  storefront: [...ROOT, "cart", "orders", "payments", "booking", "store", "support", "marketingStore"],
  /** Signed-in customer pages. */
  account: [...ROOT, "cart", "myaccount", "nav", "orders", "payments", "returns", "booking", "sessions", "shell", "store", "support"],
  /** Staff screens use almost everything. */
  admin: "all",
} as const satisfies Record<string, readonly string[] | "all">;

export type ScopeName = keyof typeof SCOPES;

type Loaded = Record<string, unknown>;
const cache = new Map<string, Promise<Loaded>>();

async function loadAll(locale: MessageLocale): Promise<Loaded> {
  const { loadMessages } = await import("./load");
  return loadMessages(locale);
}

/** The messages of one scope in one language. Only the needed fragment files are read; results are cached per language. */
export function loadScopedMessages(locale: MessageLocale, scope: ScopeName): Promise<Loaded> {
  const key = `${locale}:${scope}`;
  let hit = cache.get(key);
  if (!hit) {
    hit = (async () => {
      const wanted = SCOPES[scope];
      const all = await loadAll(locale);
      if (wanted === "all") return all;
      return Object.fromEntries(Object.entries(all).filter(([ns]) => (wanted as readonly string[]).includes(ns)));
    })();
    cache.set(key, hit);
  }
  return hit;
}

export const FRAGMENT_NAMES: readonly string[] = MESSAGE_FRAGMENTS;
