import { NextResponse, type NextRequest } from "next/server";
import { COOKIES } from "@/lib/auth/cookies";
import { backendFetch, clientHintsFrom, jsonInit } from "@/lib/server/backend";
import { clearSessionCookies } from "@/lib/server/session-cookies";

/** Ends the session at the API (revokes the refresh token family) and clears the cookies, even if the API is unreachable. */
export async function POST(request: NextRequest) {
  const refreshToken = request.cookies.get(COOKIES.refresh)?.value;
  const accessToken = request.cookies.get(COOKIES.access)?.value;

  if (refreshToken || accessToken) {
    try {
      await backendFetch("/api/v1/auth/logout", {
        method: "POST",
        token: accessToken,
        client: clientHintsFrom(request.headers),
        ...jsonInit({ refreshToken }),
      });
    } catch {
      // Cookies are cleared below either way; the refresh token also expires on its own.
    }
  }

  const response = new NextResponse(null, { status: 204, headers: { "Cache-Control": "no-store" } });
  clearSessionCookies(response.cookies);
  return response;
}
