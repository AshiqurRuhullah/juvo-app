import "server-only";
import { backendFetch, jsonInit, type ClientHints } from "./backend";

export type TokenPair = {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  refreshTokenExpiresAt: string;
  subjectType: string;
  requiresPasswordChange: boolean;
  requiresTwoFactorSetup: boolean;
};

export type RefreshResult = { ok: true; tokens: TokenPair } | { ok: false; status: number; code?: string };

type Store = {
  inflight: Map<string, Promise<RefreshResult>>;
  recent: Map<string, { at: number; result: RefreshResult }>;
};

// One store per Node process, shared by the proxy, route handlers and server components even though Next bundles them apart.
const globalStore = globalThis as unknown as { __juvoRefresh?: Store };
const store: Store = (globalStore.__juvoRefresh ??= { inflight: new Map(), recent: new Map() });

/** Requests that already left the browser with the old refresh cookie may arrive a moment later; they get the same new pair. */
const REUSE_WINDOW_MS = 10_000;

/**
 * Refreshes the session with a SINGLE in-flight request per refresh token.
 *
 * The API cuts the whole session if an already-used refresh token comes back (reuse detection). Several requests of one page
 * (server components, route handlers, parallel fetches) can notice the expired access token at the same moment; without this
 * they would each send the same refresh token and sign the user out. Callers with the same token share one promise, and for
 * a short window afterwards receive the same result instead of re-sending the used token.
 */
export function refreshTokens(refreshToken: string, hints: ClientHints = {}): Promise<RefreshResult> {
  const now = Date.now();
  for (const [key, entry] of store.recent) {
    if (now - entry.at > REUSE_WINDOW_MS) store.recent.delete(key);
  }

  const recent = store.recent.get(refreshToken);
  if (recent) return Promise.resolve(recent.result);

  const running = store.inflight.get(refreshToken);
  if (running) return running;

  const promise = callRefresh(refreshToken, hints)
    .then((result) => {
      if (result.ok) store.recent.set(refreshToken, { at: Date.now(), result });
      return result;
    })
    .finally(() => store.inflight.delete(refreshToken));
  store.inflight.set(refreshToken, promise);
  return promise;
}

async function callRefresh(refreshToken: string, hints: ClientHints): Promise<RefreshResult> {
  let response: Response;
  try {
    response = await backendFetch("/api/v1/auth/refresh", { method: "POST", client: hints, ...jsonInit({ refreshToken }) });
  } catch {
    return { ok: false, status: 503, code: "NETWORK" };
  }

  if (response.ok) return { ok: true, tokens: (await response.json()) as TokenPair };

  const problem = await response.json().catch(() => ({}));
  return { ok: false, status: response.status, code: (problem as { code?: string }).code };
}

/** Test hook: forget every remembered refresh. */
export function resetRefreshStoreForTests() {
  store.inflight.clear();
  store.recent.clear();
}
