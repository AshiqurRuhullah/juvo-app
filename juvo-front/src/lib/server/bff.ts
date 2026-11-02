import "server-only";
import { NextResponse } from "next/server";

const PASS_HEADERS = ["content-type", "x-correlation-id", "retry-after", "www-authenticate"];

/** Sends the API's answer to the browser unchanged (status, body and the few headers that matter), never cached. */
export async function relay(response: Response): Promise<NextResponse> {
  const headers = new Headers({ "Cache-Control": "no-store" });
  for (const name of PASS_HEADERS) {
    const value = response.headers.get(name);
    if (value) headers.set(name, value);
  }
  const noBody = response.status === 204 || response.status === 205 || response.status === 304;
  return new NextResponse(noBody ? null : await response.arrayBuffer(), { status: response.status, headers });
}

/** An RFC 9457 problem answer in the same shape the API uses, for failures that happen in the BFF itself. */
export function problem(status: number, code: string, title: string, extra: Record<string, unknown> = {}) {
  return NextResponse.json(
    { type: "about:blank", title, status, code, ...extra },
    { status, headers: { "Cache-Control": "no-store", "Content-Type": "application/problem+json" } },
  );
}

export function badGateway() {
  return problem(502, "GENERAL.UNREACHABLE", "The server could not be reached. Please try again in a moment.");
}

/** Reads a JSON body with a size limit; returns null when it is missing or not JSON. */
export async function readJson<T = unknown>(request: Request, limitBytes = 64 * 1024): Promise<T | null> {
  const text = await request.text();
  if (!text || text.length > limitBytes) return null;
  try {
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}
