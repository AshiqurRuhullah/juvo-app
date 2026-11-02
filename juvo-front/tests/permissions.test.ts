import { describe, expect, it } from "vitest";
import { can, canAll, canAny, canAnywhere, canInBranch, type AccessView } from "@/lib/auth/permissions";

const HO = "branch-ho";
const A = "branch-a";

const base: AccessView = { hasAllPermissions: false, globalPermissions: [], branchPermissions: {}, activeBranchId: null, isAllBranches: false };

describe("permissions mirror the server rules", () => {
  it("Super Admin holds everything, in any branch", () => {
    const admin = { ...base, hasAllPermissions: true };
    expect(can(admin, "anything.at.all")).toBe(true);
    expect(canInBranch(admin, "x", A)).toBe(true);
    expect(canAnywhere(admin, "x")).toBe(true);
  });

  it("a global role counts in every branch", () => {
    const access = { ...base, globalPermissions: ["orders.view"], activeBranchId: A };
    expect(can(access, "orders.view")).toBe(true);
    expect(can(access, "orders.edit")).toBe(false);
  });

  it("a branch role is effective only in its own branch", () => {
    const access: AccessView = { ...base, branchPermissions: { [A]: ["orders.view"] }, activeBranchId: A };
    expect(can(access, "orders.view")).toBe(true);
    expect(can({ ...access, activeBranchId: HO }, "orders.view")).toBe(false);
  });

  it("no active branch means no branch permission, unless the scope is anywhere", () => {
    const access: AccessView = { ...base, branchPermissions: { [A]: ["users.view"] } };
    expect(can(access, "users.view")).toBe(false);
    expect(can(access, "users.view", "anywhere")).toBe(true);
  });

  it("All branches mode counts any assignment", () => {
    const access: AccessView = { ...base, branchPermissions: { [A]: ["orders.view"] }, isAllBranches: true };
    expect(can(access, "orders.view")).toBe(true);
    expect(can(access, "orders.delete")).toBe(false);
  });

  it("canAny and canAll combine checks", () => {
    const access = { ...base, globalPermissions: ["a", "b"] };
    expect(canAny(access, ["x", "a"])).toBe(true);
    expect(canAny(access, ["x", "y"])).toBe(false);
    expect(canAll(access, ["a", "b"])).toBe(true);
    expect(canAll(access, ["a", "x"])).toBe(false);
    expect(canAll(access, [])).toBe(true);
  });
});
