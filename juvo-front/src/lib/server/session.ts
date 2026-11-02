import "server-only";
import { cache } from "react";
import { cookies, headers } from "next/headers";
import { redirect } from "next/navigation";
import { COOKIES } from "@/lib/auth/cookies";
import { can, type PermissionScope } from "@/lib/auth/permissions";
import type { Me } from "@/lib/api/types";
import { backendFetch } from "./backend";

export type SessionState =
  | { state: "ok"; me: Me; branchCookie: string | null }
  /** No usable access token (missing, expired, stale or the account is gone). The caller renews it or sends the user to sign in. */
  | { state: "expired" }
  | { state: "unreachable" };

/**
 * Loads who is signed in, once per request. Reads the httpOnly access cookie and the branch cookie and asks the API
 * (/auth/me), which is the single source of truth for roles and permissions.
 */
export const loadSession = cache(async (): Promise<SessionState> => {
  const jar = await cookies();
  const token = jar.get(COOKIES.access)?.value;
  if (!token) return { state: "expired" };

  const branchCookie = jar.get(COOKIES.branch)?.value ?? null;
  const call = (branch: string | null) => backendFetch("/api/v1/auth/me", { token, branch: branch ?? undefined });

  try {
    let response = await call(branchCookie);
    if (response.status === 403 && branchCookie) {
      // The remembered branch is not (or no longer) the user's: fall back to no branch rather than locking them out.
      const problem = (await response.clone().json().catch(() => ({}))) as { code?: string };
      if (problem.code === "AUTH.BRANCH_FORBIDDEN") response = await call(null);
    }
    if (response.status === 401) return { state: "expired" };
    if (!response.ok) return { state: "unreachable" };
    return { state: "ok", me: (await response.json()) as Me, branchCookie };
  } catch {
    return { state: "unreachable" };
  }
});

/** The path the user was heading to, set by proxy.ts, so we can send them back after a refresh or sign-in. */
async function currentPath(): Promise<string> {
  return (await headers()).get("x-pathname") ?? "/admin";
}

async function toRefreshOrLogin(): Promise<never> {
  const path = await currentPath();
  const jar = await cookies();
  // A refresh just happened and we are still not authenticated: do not loop, ask for a fresh sign-in.
  if (jar.get(COOKIES.refreshed)) redirect(`/login?next=${encodeURIComponent(path)}&reason=expired`);
  redirect(`/api/bff/auth/refresh?next=${encodeURIComponent(path)}`);
}

/** Sends limited sessions (password change or two-step setup pending) to the first-login flow. */
function needsFirstLogin(me: Me) {
  const staff = me.staff;
  return !!staff && (staff.mustChangePassword || staff.requiresTwoFactorSetup);
}

export type RequireOptions = { subject: "staff" | "customer"; allowLimited?: boolean };

/** For protected layouts and pages: returns the signed-in person or redirects. Never returns for anyone else. */
export async function requireSession(options: RequireOptions): Promise<Extract<SessionState, { state: "ok" }>> {
  const session = await loadSession();
  if (session.state === "unreachable") throw new Error("The server could not be reached.");
  if (session.state === "expired") return toRefreshOrLogin();

  if (session.me.subjectType !== options.subject) redirect("/forbidden");
  if (options.subject === "staff" && !options.allowLimited && needsFirstLogin(session.me)) redirect("/first-login");
  return session;
}

/** For pages that need a permission: 403 page when the user lacks it in the active branch. */
export function requirePermission(me: Me, permission: string, scope: PermissionScope = "active") {
  if (!can(me, permission, scope)) redirect("/forbidden");
}
