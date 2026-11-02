import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { COOKIES, preferenceCookieOptions } from "@/lib/auth/cookies";
import { problem, readJson } from "@/lib/server/bff";

const bodySchema = z.object({
  locale: z.enum(["en", "bn"]).optional(),
  theme: z.enum(["light", "dark", "system"]).optional(),
  sidebar: z.enum(["expanded", "collapsed"]).optional(),
});

/** Language, theme and sidebar state. Plain preference cookies, readable by the server so the first paint is already right. */
export async function POST(request: NextRequest) {
  const parsed = bodySchema.safeParse(await readJson(request));
  if (!parsed.success) return problem(400, "GENERAL.BAD_REQUEST", "Unknown preference.");

  const response = new NextResponse(null, { status: 204, headers: { "Cache-Control": "no-store" } });
  const { locale, theme, sidebar } = parsed.data;
  if (locale) response.cookies.set(COOKIES.locale, locale, preferenceCookieOptions());
  if (theme) response.cookies.set(COOKIES.theme, theme, preferenceCookieOptions());
  if (sidebar) response.cookies.set(COOKIES.sidebar, sidebar, preferenceCookieOptions());
  return response;
}
