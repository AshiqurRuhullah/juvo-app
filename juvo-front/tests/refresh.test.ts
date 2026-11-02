import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("server-only", () => ({}));
const backendFetch = vi.fn();
vi.mock("@/lib/server/backend", () => ({
  backendFetch: (...args: unknown[]) => backendFetch(...args),
  jsonInit: (body: unknown) => ({ body: JSON.stringify(body) }),
}));

import { refreshTokens, resetRefreshStoreForTests } from "@/lib/server/refresh";

const pair = (n: number) => ({
  accessToken: `at-${n}`,
  accessTokenExpiresAt: "2030-01-01T00:00:00Z",
  refreshToken: `rt-${n}`,
  refreshTokenExpiresAt: "2030-02-01T00:00:00Z",
  subjectType: "staff",
  requiresPasswordChange: false,
  requiresTwoFactorSetup: false,
});

const ok = (n: number) => new Response(JSON.stringify(pair(n)), { status: 200, headers: { "content-type": "application/json" } });

beforeEach(() => {
  backendFetch.mockReset();
  resetRefreshStoreForTests();
});

describe("refreshTokens", () => {
  it("sends ONE request when many callers refresh with the same token at once", async () => {
    backendFetch.mockImplementation(async () => {
      await new Promise((r) => setTimeout(r, 20));
      return ok(2);
    });
    const results = await Promise.all(Array.from({ length: 8 }, () => refreshTokens("rt-1")));
    expect(backendFetch).toHaveBeenCalledTimes(1);
    for (const r of results) expect(r.ok && r.tokens.refreshToken).toBe("rt-2");
  });

  it("a late request carrying the already used token gets the same new pair, not a second call", async () => {
    backendFetch.mockImplementation(async () => ok(2));
    await refreshTokens("rt-1");
    const late = await refreshTokens("rt-1");
    expect(backendFetch).toHaveBeenCalledTimes(1);
    expect(late.ok && late.tokens.refreshToken).toBe("rt-2");
  });

  it("different tokens refresh independently", async () => {
    backendFetch.mockResolvedValueOnce(ok(2)).mockResolvedValueOnce(ok(4));
    await refreshTokens("rt-1");
    await refreshTokens("rt-3");
    expect(backendFetch).toHaveBeenCalledTimes(2);
  });

  it("a rejected refresh reports status and code, and is not cached as success", async () => {
    backendFetch.mockImplementation(async () => new Response(JSON.stringify({ code: "Auth.RefreshTokenReused" }), { status: 401 }));
    const first = await refreshTokens("rt-1");
    expect(first).toEqual({ ok: false, status: 401, code: "Auth.RefreshTokenReused" });
    await refreshTokens("rt-1");
    expect(backendFetch).toHaveBeenCalledTimes(2);
  });

  it("a network failure becomes a 503 result instead of throwing", async () => {
    backendFetch.mockRejectedValue(new Error("connect ECONNREFUSED"));
    expect(await refreshTokens("rt-1")).toEqual({ ok: false, status: 503, code: "NETWORK" });
  });
});
