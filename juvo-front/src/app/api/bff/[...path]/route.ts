import { NextResponse, type NextRequest } from "next/server";
import { CART_COOKIE_MAX_AGE, COOKIES, isExpiring, isSecureContext } from "@/lib/auth/cookies";
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

  // Guest cart: the token lives in an httpOnly cookie; the proxy adds it as X-Cart-Token and stores a new one from the answer.
  // Checkout and its quote read the same cart, so they get the header too (a successful checkout spends the cart).
  const isCart = target === "/api/v1/store/cart" || target.startsWith("/api/v1/store/cart/");
  const isCheckout = target === "/api/v1/store/checkout" || target.startsWith("/api/v1/store/checkout/");
  const cartToken = isCart || isCheckout ? request.cookies.get(COOKIES.cart)?.value : undefined;
  // Makes a retried checkout safe: the same key returns the same order.
  const idempotencyKey = isCheckout ? request.headers.get("idempotency-key")?.slice(0, 80) || undefined : undefined;

  const contentType = request.headers.get("content-type") ?? undefined;
  const idempotencyKey = request.headers.get("idempotency-key") ?? undefined;
  const hasBody = !["GET", "HEAD"].includes(request.method);
  const body = hasBody ? await request.arrayBuffer() : undefined;

  const call = (token: string | undefined) =>
    backendFetch(target + request.nextUrl.search, {
      method: request.method,
      token,
      branch,
      client: hints,
      headers: { ...(contentType ? { "Content-Type": contentType } : {}), ...(cartToken ? { "X-Cart-Token": cartToken } : {}), ...(idempotencyKey ? { "Idempotency-Key": idempotencyKey } : {}) },
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

  const cartAnswer = isCart && upstream.ok && (upstream.headers.get("content-type") ?? "").includes("json") ? upstream.clone() : null;
  let response = await relay(upstream);
  if (cartAnswer) {
    const answer = (await cartAnswer.json().catch(() => null)) as { token?: unknown } | null;
    if (answer && typeof answer.token === "string") {
      const { token, ...rest } = answer;
      response = NextResponse.json(rest, { status: upstream.status, headers: { "Cache-Control": "no-store" } });
      response.cookies.set(COOKIES.cart, token, { httpOnly: true, secure: isSecureContext(), sameSite: "lax", path: "/", maxAge: CART_COOKIE_MAX_AGE });
    }
  }
  // A placed order spends the cart: forget its token so the next visit starts a fresh cart.
  if (target === "/api/v1/store/checkout" && request.method === "POST" && upstream.status === 201) response.cookies.delete(COOKIES.cart);
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
