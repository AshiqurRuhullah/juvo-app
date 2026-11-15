import { NextResponse } from "next/server";
import { apiBaseUrl } from "@/lib/server/backend";

/**
 * The address the browser connects its live-update (SignalR) socket to. Read at run time, not build time, so one build works on
 * every machine. Set PUBLIC_API_URL when the browser must reach the API at another address than this server does (for example
 * behind a reverse proxy); otherwise it is API_BASE_URL. The API's CORS policy must allow this site's origin.
 */
export function GET() {
  const url = (process.env.PUBLIC_API_URL ?? apiBaseUrl()).replace(/\/+$/, "");
  return NextResponse.json({ url }, { headers: { "Cache-Control": "private, max-age=300" } });
}
