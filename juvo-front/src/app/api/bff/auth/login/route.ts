import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { backendFetch, clientHintsFrom, jsonInit } from "@/lib/server/backend";
import { badGateway, problem, readJson, relay } from "@/lib/server/bff";
import { clearSessionCookies, publicTokenInfo, writeSessionCookies } from "@/lib/server/session-cookies";
import type { TokenPair } from "@/lib/server/refresh";

const bodySchema = z.object({
  identifier: z.string().min(1).max(200),
  password: z.string().min(1).max(200),
  totpCode: z.string().max(20).optional().nullable(),
  recoveryCode: z.string().max(40).optional().nullable(),
  deviceName: z.string().max(100).optional().nullable(),
});

/** Staff sign-in. The tokens go into httpOnly cookies; the browser only learns what to show next. */
export async function POST(request: NextRequest) {
  const parsed = bodySchema.safeParse(await readJson(request));
  if (!parsed.success) return problem(400, "GENERAL.BAD_REQUEST", "Enter your email or user name and your password.");

  let response: Response;
  try {
    response = await backendFetch("/api/v1/auth/login", {
      method: "POST",
      client: clientHintsFrom(request.headers),
      ...jsonInit(parsed.data),
    });
  } catch {
    return badGateway();
  }
  if (!response.ok) return relay(response);

  const tokens = (await response.json()) as TokenPair;
  const result = NextResponse.json(publicTokenInfo(tokens), { headers: { "Cache-Control": "no-store" } });
  clearSessionCookies(result.cookies); // drop any older session first
  writeSessionCookies(result.cookies, tokens);
  return result;
}
