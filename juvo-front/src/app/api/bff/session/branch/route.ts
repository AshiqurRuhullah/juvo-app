import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { COOKIES, preferenceCookieOptions } from "@/lib/auth/cookies";
import { problem, readJson } from "@/lib/server/bff";

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const bodySchema = z.object({ branch: z.union([z.literal("all"), z.string().regex(uuid), z.null()]) });

/**
 * Remembers the active branch ("all" or a branch id) for the BFF to send as X-Branch-Id. The value is only a preference:
 * the API validates it against the user's assignments on every request and refuses anything the user does not hold.
 */
export async function POST(request: NextRequest) {
  const parsed = bodySchema.safeParse(await readJson(request));
  if (!parsed.success) return problem(400, "GENERAL.BAD_REQUEST", "Choose a branch.");

  const response = new NextResponse(null, { status: 204, headers: { "Cache-Control": "no-store" } });
  if (parsed.data.branch === null) {
    response.cookies.set(COOKIES.branch, "", { ...preferenceCookieOptions(), maxAge: 0 });
  } else {
    response.cookies.set(COOKIES.branch, parsed.data.branch, { ...preferenceCookieOptions(), maxAge: 60 * 60 * 24 * 30 });
  }
  return response;
}
