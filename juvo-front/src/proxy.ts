import { NextResponse, type NextRequest } from "next/server";
import { COOKIES, isExpiring } from "@/lib/auth/cookies";

/**
 * Route protection (Next.js "proxy", formerly middleware). Runs before pages in the admin and account areas:
 *
 *  - no valid access token  ->  /api/bff/auth/refresh?next=<path>, which renews the session with the single in-flight refresh
 *    (or lands on /login if there is nothing to renew);
 *  - otherwise the request continues, with the path passed on in x-pathname so layouts know where to return after a redirect.
 *
 * This only decides about presence and expiry of the cookie. The API verifies every token, and the layouts load /auth/me and
 * check permissions, so a forged cookie gets nothing.
 */
export function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  const access = request.cookies.get(COOKIES.access)?.value;

  if (isExpiring(access)) {
    const url = request.nextUrl.clone();
    url.pathname = "/api/bff/auth/refresh";
    url.search = `?next=${encodeURIComponent(pathname + search)}`;
    return NextResponse.redirect(url);
  }

  const headers = new Headers(request.headers);
  headers.set("x-pathname", pathname + search);
  return NextResponse.next({ request: { headers } });
}

export const config = {
  matcher: ["/admin/:path*", "/account/:path*", "/first-login"],
};
