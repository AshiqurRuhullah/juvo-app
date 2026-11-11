/** Cookie names and options. Tokens are only ever in httpOnly cookies set by the BFF; JavaScript in the browser never reads them. */
export const COOKIES = {
  /** Access token. httpOnly, lives as long as the token (15 min). */
  access: "juvo_at",
  /** Refresh token. httpOnly, only sent to /api/bff (the BFF), never to page requests. */
  refresh: "juvo_rt",
  /** Marks "a refresh just happened" for 15 s, to stop a redirect loop if /auth/me still fails. */
  refreshed: "juvo_rf",
  /** Active branch: a branch id or "all". Validated by the API on every request. */
  branch: "juvo_branch",
  locale: "juvo_locale",
  theme: "juvo_theme",
  sidebar: "juvo_sidebar",
  /** Guest cart token. httpOnly: the BFF adds it to cart calls, so page scripts never hold it. Cleared after sign-in (the cart is merged). */
  cart: "juvo_cart",
} as const;

/** A guest cart lives as long as the API keeps it (30 days after the last change). */
export const CART_COOKIE_MAX_AGE = 60 * 60 * 24 * 30;

export const REFRESH_COOKIE_PATH = "/api/bff";

export type CookieOptions = {
  httpOnly: boolean;
  secure: boolean;
  sameSite: "lax" | "strict" | "none";
  path: string;
  expires?: Date;
  maxAge?: number;
};

/** Secure cookies in production. Browsers accept Secure on http://localhost, but Safari does not, so dev stays plain. */
export function isSecureContext(): boolean {
  return process.env.NODE_ENV === "production" && process.env.COOKIE_SECURE !== "false";
}

export function accessCookieOptions(expiresAt: Date): CookieOptions {
  return { httpOnly: true, secure: isSecureContext(), sameSite: "lax", path: "/", expires: expiresAt };
}

export function refreshCookieOptions(expiresAt: Date): CookieOptions {
  return { httpOnly: true, secure: isSecureContext(), sameSite: "lax", path: REFRESH_COOKIE_PATH, expires: expiresAt };
}

/** Readable by the browser (preferences, not secrets). */
export function preferenceCookieOptions(): CookieOptions {
  return { httpOnly: false, secure: isSecureContext(), sameSite: "lax", path: "/", maxAge: 60 * 60 * 24 * 365 };
}

/** Decodes a JWT payload WITHOUT verifying it. Only used to decide when to refresh; the API verifies every token. */
export function decodeJwt(token: string): { exp?: number; stype?: string; scope?: string; sid?: string } | null {
  const part = token.split(".")[1];
  if (!part) return null;
  try {
    const json = atob(part.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(part.length / 4) * 4, "="));
    return JSON.parse(json);
  } catch {
    return null;
  }
}

/** True when the token is missing, malformed, expired or expires within `skewSeconds`. */
export function isExpiring(token: string | undefined, skewSeconds = 20): boolean {
  if (!token) return true;
  const exp = decodeJwt(token)?.exp;
  if (!exp) return true;
  return exp * 1000 - Date.now() < skewSeconds * 1000;
}
