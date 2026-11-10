import { NextResponse, type NextRequest } from "next/server";
import { backendFetch } from "@/lib/server/backend";

/**
 * Product pictures. The API stores them under /files and gives every file a unique name, so they are public and can be cached for a
 * year. The browser asks this app (same origin, no CORS, no API address in the page); this route fetches the file from the API at
 * request time, so the API address can differ between environments without a rebuild.
 */
const SAFE_PATH = /^[a-z0-9][a-z0-9/_.-]*$/i;
const TYPES: Record<string, string> = { webp: "image/webp", png: "image/png", jpg: "image/jpeg", jpeg: "image/jpeg" };

export async function GET(_request: NextRequest, context: { params: Promise<{ path: string[] }> }) {
  const { path } = await context.params;
  const joined = path.join("/");
  const extension = joined.split(".").pop()?.toLowerCase() ?? "";
  if (!SAFE_PATH.test(joined) || joined.includes("..") || !TYPES[extension]) return new NextResponse(null, { status: 404 });

  try {
    const upstream = await backendFetch(`/files/${path.map(encodeURIComponent).join("/")}`);
    if (!upstream.ok) return new NextResponse(null, { status: upstream.status === 404 ? 404 : 502 });
    return new NextResponse(await upstream.arrayBuffer(), {
      status: 200,
      headers: { "Content-Type": TYPES[extension]!, "Cache-Control": "public, max-age=31536000, immutable", "X-Content-Type-Options": "nosniff" },
    });
  } catch {
    return new NextResponse(null, { status: 502 });
  }
}
