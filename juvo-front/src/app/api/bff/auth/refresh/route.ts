import { NextResponse, type NextRequest } from "next/server";
import { COOKIES } from "@/lib/auth/cookies";
import { clientHintsFrom } from "@/lib/server/backend";
import { refreshTokens } from "@/lib/server/refresh";
import { clearSessionCookies, markRefreshed, writeSessionCookies } from "@/lib/server/session-cookies";
import { problem } from "@/lib/server/bff";
import { safeNext } from "@/lib/utils";

/**
 * Page navigations that arrive without a valid access token are sent here (by proxy.ts or a layout). We refresh with the
 * single in-flight refresh, set the new cookies, and send the browser back to where it was going. If the refresh token is
 * missing, expired or was reused, the session is cleared and the user lands on the login page.
 */
export async function GET(request: NextRequest) {
  const next = safeNext(request.nextUrl.searchParams.get("next"), "/admin");
  const refresh = request.cookies.get(COOKIES.refresh)?.value;

  const toLogin = () => {
    const url = new URL("/login", request.url);
    url.searchParams.set("next", next);
    url.searchParams.set("reason", "expired");
    const response = NextResponse.redirect(url);
    clearSessionCookies(response.cookies);
    return response;
  };

  if (!refresh) return toLogin();

  const result = await refreshTokens(refresh, clientHintsFrom(request.headers));
  if (!result.ok) return toLogin();

  const response = NextResponse.redirect(new URL(next, request.url));
  writeSessionCookies(response.cookies, result.tokens);
  markRefreshed(response.cookies);
  return response;
}

/** Used by browser code that wants to renew the session without navigating. 204 = renewed, 401 = sign in again. */
export async function POST(request: NextRequest) {
  const refresh = request.cookies.get(COOKIES.refresh)?.value;
  if (!refresh) return problem(401, "AUTH.UNAUTHORIZED", "Please sign in.");

  const result = await refreshTokens(refresh, clientHintsFrom(request.headers));
  if (!result.ok) {
    const response = problem(401, result.code ?? "AUTH.UNAUTHORIZED", "Please sign in again.");
    clearSessionCookies(response.cookies);
    return response;
  }

  const response = new NextResponse(null, { status: 204, headers: { "Cache-Control": "no-store" } });
  writeSessionCookies(response.cookies, result.tokens);
  return response;
}
