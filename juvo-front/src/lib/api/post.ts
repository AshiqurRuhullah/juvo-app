import { toProblem } from "./problem";
import type { ApiProblem } from "./types";

export type JsonResult<T> = { ok: true; status: number; data: T } | { ok: false; status: number; problem: ApiProblem };

/**
 * JSON call to our own server (the BFF). Used for the few endpoints that set cookies (login, change-password, OTP verify,
 * logout, branch and preferences). Everything else goes through the typed client in ./client.
 */
export async function requestJson<T = unknown>(method: string, url: string, body?: unknown): Promise<JsonResult<T>> {
  try {
    const response = await fetch(url, {
      method,
      credentials: "same-origin",
      headers: body === undefined ? undefined : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    const parsed = text ? safeParse(text) : undefined;
    if (response.ok) return { ok: true, status: response.status, data: parsed as T };
    return { ok: false, status: response.status, problem: toProblem(parsed, response.status) };
  } catch {
    return { ok: false, status: 0, problem: { status: 0, code: "NETWORK", title: "Network error" } };
  }
}

export const postJson = <T = unknown>(url: string, body?: unknown) => requestJson<T>("POST", url, body ?? {});

function safeParse(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return undefined;
  }
}
