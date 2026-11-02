import { NextResponse, type NextRequest } from "next/server";
import { COOKIES, isExpiring } from "@/lib/auth/cookies";
import { backendFetch, clientHintsFrom } from "@/lib/server/backend";
import { badGateway, problem, relay } from "@/lib/server/bff";
import { refreshTokens, type TokenPair } from "@/lib/server/refresh";
import { clearSessionCookies, writeSessionCookies } from "@/lib/server/session-cookies";

/**
 * Backend-for-frontend proxy. The browser calls /api/bff/api/v1/... (same origin, so no CORS and the httpOnly cookies ride
 * along); we add the bearer token and X-Branch-Id and forward to the API. An expired access token is renewed with the single
 * in-flight refresh and the call is retried once. Tokens never reach the browser.
 */

// Endpoints that return tokens have dedicated routes that put them in cookies. They must not be reachable through here, or the
// tokens would be handed to browser JavaScript.
const BLOCKED = new Set([
  "/api/v1/auth/login",
  "/api/v1/auth/refresh",
  "/api/v1/auth/logout",
  "/api/v1/auth/change-password",
  "/api/v1/auth/otp/verify",
]);

type Context = { params: Promise<{ path: string[] }> };

async function handle(request: NextRequest, context: Context) {
  const { path } = await context.params;
  const target = "/" + path.map(encodeURIComponent).join("/");

  if (!target.startsWith("/api/v1/") || BLOCKED.has(target.replace(/\/+$/, "").toLowerCase()) || path.some((p) => p === ".." || p === ".")) {
    return problem(404, "GENERAL.NOT_FOUND", "Not found.");
  }

  const hints = clientHintsFrom(request.headers);
  const branch = request.cookies.get(COOKIES.branch)?.value;
  const refresh = request.cookies.get(COOKIES.refresh)?.value;
  let access = request.cookies.get(COOKIES.access)?.value;
  let renewed: TokenPair | undefined;

  const contentType = request.headers.get("content-type") ?? undefined;
  const hasBody = !["GET", "HEAD"].includes(request.method);
  const body = hasBody ? await request.arrayBuffer() : undefined;

  const call = (token: string | undefined) =>
    backendFetch(target + request.nextUrl.search, {
      method: request.method,
      token,
      branch,
      client: hints,
      headers: contentType ? { "Content-Type": contentType } : undefined,
      body,
    });

  const renew = async () => {
    if (!refresh) return false;
    const result = await refreshTokens(refresh, hints);
    if (!result.ok) return false;
    renewed = result.tokens;
    access = result.tokens.accessToken;
    return true;
  };

  const expired = () => {
    const response = problem(401, "AUTH.TOKEN_EXPIRED", "Your session has ended. Please sign in again.");
    clearSessionCookies(response.cookies);
    return response;
  };

  // Renew before the call when the access token is missing or about to expire.
  if (isExpiring(access) && refresh && !(await renew())) return expired();

  let upstream: Response;
  try {
    upstream = await call(access);
    if (upstream.status === 401 && refresh && !renewed) {
      // The token was refused (stale permission version or just expired): renew once and retry.
      const code = (await upstream.clone().json().catch(() => ({})) as { code?: string }).code;
      if (code === "AUTH.TOKEN_EXPIRED" || code === "AUTH.UNAUTHORIZED") {
        if (!(await renew())) return expired();
        upstream = await call(access);
      }
    }
  } catch {
    return badGateway();
  }

  const response = await relay(upstream);
  if (renewed) writeSessionCookies(response.cookies, renewed);
  return response;
}

export const GET = handle;
export const POST = handle;
export const PUT = handle;
export const PATCH = handle;
export const DELETE = handle;

export function OPTIONS() {
  return new NextResponse(null, { status: 405, headers: { Allow: "GET, POST, PUT, PATCH, DELETE" } });
}
