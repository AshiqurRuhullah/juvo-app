import createClient, { type Middleware } from "openapi-fetch";
import type { paths } from "./schema";
import { sessionEnded, toProblem } from "./problem";
import { hardNavigate } from "@/lib/navigation";

/**
 * Typed API client for browser code. Every call goes to the same-origin BFF (/api/bff), which adds the token and the active
 * branch, so this file never sees a token. Paths are the API's own: api.GET("/api/v1/auth/me").
 */
export const api = createClient<paths>({ baseUrl: "/api/bff" });

let redirecting = false;

/** When the session is really over (the BFF already tried to renew it), go to the login page and come back afterwards. */
const sessionGuard: Middleware = {
  async onResponse({ response }) {
    if (response.status === 401 && typeof window !== "undefined" && !redirecting) {
      const problem = toProblem(await response.clone().json().catch(() => ({})), 401);
      if (sessionEnded(problem)) {
        redirecting = true;
        const next = encodeURIComponent(window.location.pathname + window.location.search);
        hardNavigate(`/login?next=${next}&reason=expired`);
      }
    }
    return undefined;
  },
};

api.use(sessionGuard);

/** Throws an Error carrying the API's problem so TanStack Query hooks can show it. */
export class ApiError extends Error {
  constructor(public readonly problem: ReturnType<typeof toProblem>) {
    super(problem.title ?? "Request failed");
  }
}

/** Unwraps an openapi-fetch result: the data, or an ApiError. */
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (result.error !== undefined || result.data === undefined) {
    if (result.response.status === 204 || (result.response.ok && result.error === undefined)) return undefined as T;
    throw new ApiError(toProblem(result.error, result.response.status));
  }
  return result.data;
}
