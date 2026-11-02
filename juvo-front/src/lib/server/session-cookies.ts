import "server-only";
import { accessCookieOptions, COOKIES, refreshCookieOptions, REFRESH_COOKIE_PATH, isSecureContext } from "@/lib/auth/cookies";
import type { TokenPair } from "./refresh";

/** The part of NextResponse.cookies / cookies() we use, so one helper serves both. */
export type CookieJar = {
  set(name: string, value: string, options?: Record<string, unknown>): unknown;
};

export function writeSessionCookies(jar: CookieJar, tokens: TokenPair) {
  jar.set(COOKIES.access, tokens.accessToken, accessCookieOptions(new Date(tokens.accessTokenExpiresAt)));
  jar.set(COOKIES.refresh, tokens.refreshToken, refreshCookieOptions(new Date(tokens.refreshTokenExpiresAt)));
}

export function clearSessionCookies(jar: CookieJar) {
  const base = { httpOnly: true, secure: isSecureContext(), sameSite: "lax" as const, maxAge: 0, expires: new Date(0) };
  jar.set(COOKIES.access, "", { ...base, path: "/" });
  jar.set(COOKIES.refresh, "", { ...base, path: REFRESH_COOKIE_PATH });
  jar.set(COOKIES.branch, "", { ...base, httpOnly: false, path: "/" });
}

/** Marks a refresh that just happened, to stop a redirect loop if the next request still fails. */
export function markRefreshed(jar: CookieJar) {
  jar.set(COOKIES.refreshed, "1", { httpOnly: true, secure: isSecureContext(), sameSite: "lax", path: "/", maxAge: 15 });
}

/** The browser gets what it needs to draw the UI, never the tokens. */
export function publicTokenInfo(tokens: TokenPair) {
  return {
    subjectType: tokens.subjectType,
    requiresPasswordChange: tokens.requiresPasswordChange,
    requiresTwoFactorSetup: tokens.requiresTwoFactorSetup,
  };
}
