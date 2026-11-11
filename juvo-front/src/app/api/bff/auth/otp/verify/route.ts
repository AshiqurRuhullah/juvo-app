import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { COOKIES, isSecureContext } from "@/lib/auth/cookies";
import { backendFetch, clientHintsFrom, jsonInit } from "@/lib/server/backend";
import { badGateway, problem, readJson, relay } from "@/lib/server/bff";
import type { TokenPair } from "@/lib/server/refresh";
import { clearSessionCookies, publicTokenInfo, writeSessionCookies } from "@/lib/server/session-cookies";

const bodySchema = z.object({
  mobile: z.string().min(5).max(30),
  code: z.string().min(4).max(10),
  deviceName: z.string().max(100).optional().nullable(),
  email: z.string().email().max(254).optional().nullable(),
});

/** Customer sign-in with mobile number and one-time password. Same cookie handling as the staff login. */
export async function POST(request: NextRequest) {
  const parsed = bodySchema.safeParse(await readJson(request));
  if (!parsed.success) return problem(400, "GENERAL.BAD_REQUEST", "Enter your mobile number and the code we sent.");

  let response: Response;
  try {
    response = await backendFetch("/api/v1/auth/otp/verify", { method: "POST", client: clientHintsFrom(request.headers), ...jsonInit(parsed.data) });
  } catch {
    return badGateway();
  }
  if (!response.ok) return relay(response);

  const tokens = (await response.json()) as TokenPair;

  // A guest cart (kept in an httpOnly cookie) is merged into the customer's cart now; a failure here never blocks the sign-in.
  const guestCart = request.cookies.get(COOKIES.cart)?.value;
  if (guestCart && tokens.subjectType === "customer") {
    await backendFetch("/api/v1/store/cart/merge", { method: "POST", token: tokens.accessToken, client: clientHintsFrom(request.headers), ...jsonInit({ token: guestCart }) }).catch(() => undefined);
  }

  const result = NextResponse.json(publicTokenInfo(tokens), { headers: { "Cache-Control": "no-store" } });
  if (guestCart) result.cookies.set(COOKIES.cart, "", { httpOnly: true, secure: isSecureContext(), sameSite: "lax", path: "/", maxAge: 0 });
  clearSessionCookies(result.cookies);
  writeSessionCookies(result.cookies, tokens);
  return result;
}
