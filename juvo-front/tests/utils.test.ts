import { describe, expect, it } from "vitest";
import { initials, safeNext } from "@/lib/utils";
import { decodeJwt, isExpiring } from "@/lib/auth/cookies";

function jwt(payload: object) {
  const enc = (o: object) => Buffer.from(JSON.stringify(o)).toString("base64url");
  return `${enc({ alg: "HS256" })}.${enc(payload)}.sig`;
}

describe("safeNext (open redirect guard)", () => {
  it.each(["/admin", "/admin/users?x=1", "/"])("allows %s", (p) => expect(safeNext(p, "/fallback")).toBe(p));
  it.each(["https://evil.test", "//evil.test", "/\\evil.test", "javascript:alert(1)", "admin", "/a\nb", "/a\rb", "", null, undefined])(
    "rejects %j",
    (p) => expect(safeNext(p as string | null | undefined, "/fallback")).toBe("/fallback"),
  );
});

describe("initials", () => {
  it("uses first and last name", () => expect(initials("Jane Anne Doe")).toBe("JD"));
  it("handles one word, spaces and empty", () => {
    expect(initials("  admin ")).toBe("A");
    expect(initials("")).toBe("?");
    expect(initials(null)).toBe("?");
  });
});

describe("access token timing", () => {
  const inSeconds = (s: number) => Math.floor(Date.now() / 1000) + s;
  it("reads the claims without verifying", () => expect(decodeJwt(jwt({ exp: 5, stype: "staff" }))).toMatchObject({ exp: 5, stype: "staff" }));
  it("returns null for garbage", () => {
    expect(decodeJwt("nope")).toBeNull();
    expect(decodeJwt("a.!!!.c")).toBeNull();
  });
  it("is expiring when missing, malformed, expired or about to expire", () => {
    expect(isExpiring(undefined)).toBe(true);
    expect(isExpiring("garbage")).toBe(true);
    expect(isExpiring(jwt({}))).toBe(true);
    expect(isExpiring(jwt({ exp: inSeconds(-5) }))).toBe(true);
    expect(isExpiring(jwt({ exp: inSeconds(10) }))).toBe(true);
  });
  it("is fresh when well within its lifetime", () => expect(isExpiring(jwt({ exp: inSeconds(600) }))).toBe(false));
});
