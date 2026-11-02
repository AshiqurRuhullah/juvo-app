import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { COOKIES, isExpiring } from "@/lib/auth/cookies";
import { backendFetch, clientHintsFrom, jsonInit } from "@/lib/server/backend";
import { badGateway, problem, readJson, relay } from "@/lib/server/bff";
import { refreshTokens, type TokenPair } from "@/lib/server/refresh";
import { clearSessionCookies, publicTokenInfo, writeSessionCookies } from "@/lib/server/session-cookies";

const bodySchema = z.object({
  currentPassword: z.string().min(1).max(200),
  newPassword: z.string().min(1).max(200),
});

/**
 * Changing the password ends every session and the API returns a brand new one. This route stores that new pair in the cookies,
 * so the user stays signed in on this device (and the forced first-login flow can continue).
 */
export async function POST(request: NextRequest) {
  const parsed = bodySchema.safeParse(await readJson(request));
  if (!parsed.success) return problem(400, "GENERAL.BAD_REQUEST", "Enter your current and your new password.");

  const hints = clientHintsFrom(request.headers);
  let access = request.cookies.get(COOKIES.access)?.value;
  const refresh = request.cookies.get(COOKIES.refresh)?.value;
  let renewed: TokenPair | undefined;

  if (isExpiring(access) && refresh) {
    const result = await refreshTokens(refresh, hints);
    if (!result.ok) return problem(401, result.code ?? "AUTH.UNAUTHORIZED", "Please sign in again.");
    renewed = result.tokens;
    access = renewed.accessToken;
  }
  if (!access) return problem(401, "AUTH.UNAUTHORIZED", "Please sign in.");

  let response: Response;
  try {
    response = await backendFetch("/api/v1/auth/change-password", { method: "POST", token: access, client: hints, ...jsonInit(parsed.data) });
  } catch {
    return badGateway();
  }

  if (!response.ok) {
    const failed = await relay(response);
    if (renewed) writeSessionCookies(failed.cookies, renewed);
    return failed;
  }

  const tokens = (await response.json()) as TokenPair;
  const result = NextResponse.json(publicTokenInfo(tokens), { headers: { "Cache-Control": "no-store" } });
  clearSessionCookies(result.cookies);
  writeSessionCookies(result.cookies, tokens);
  return result;
}
