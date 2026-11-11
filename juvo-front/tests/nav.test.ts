import { describe, expect, it } from "vitest";
import { ACCOUNT_NAV, ADMIN_NAV, activeItem, flattenNav, isLeafGroup, visibleNav } from "@/lib/nav";
import type { AccessView } from "@/lib/auth/permissions";
import en from "@/messages/en.json";
import bn from "@/messages/bn.json";

const none: AccessView = { hasAllPermissions: false, globalPermissions: [], branchPermissions: {}, activeBranchId: null, isAllBranches: false };
const superAdmin: AccessView = { ...none, hasAllPermissions: true };

describe("menu visibility", () => {
  it("a user with no permissions sees only items that need none, and no empty groups", () => {
    const groups = visibleNav(ADMIN_NAV, none);
    for (const item of flattenNav(groups)) expect(item.permission).toBeUndefined();
    for (const group of groups) expect(group.items.length).toBeGreaterThan(0);
  });

  it("Super Admin sees every item", () => {
    expect(flattenNav(visibleNav(ADMIN_NAV, superAdmin)).length).toBe(flattenNav(ADMIN_NAV).length);
  });

  it("an item appears only where the permission is held (active branch)", () => {
    const item = flattenNav(ADMIN_NAV).find((i) => i.permission && (i.scope ?? "active") === "active")!;
    const inA: AccessView = { ...none, branchPermissions: { a: [item.permission!] }, activeBranchId: "a" };
    expect(flattenNav(visibleNav(ADMIN_NAV, inA)).map((i) => i.id)).toContain(item.id);
    expect(flattenNav(visibleNav(ADMIN_NAV, { ...inA, activeBranchId: "b" })).map((i) => i.id)).not.toContain(item.id);
  });

  it("ids are unique and hrefs are absolute paths", () => {
    const all = [...flattenNav(ADMIN_NAV), ...flattenNav(ACCOUNT_NAV)];
    expect(new Set(all.map((i) => i.id)).size).toBe(all.length);
    for (const item of all) expect(item.href.startsWith("/")).toBe(true);
  });

  it("the active item is the longest matching route", () => {
    expect(activeItem(ADMIN_NAV, "/admin")?.href).toBe("/admin");
    const nested = flattenNav(ADMIN_NAV).find((i) => i.href.split("/").length > 3);
    if (nested) expect(activeItem(ADMIN_NAV, `${nested.href}/123`)?.href).toBe(nested.href);
    expect(activeItem(ADMIN_NAV, "/admin/does-not-exist")?.href).not.toBe("/admin");
  });

  it("every group has an icon, and a one-item group is a plain link", () => {
    for (const group of [...ADMIN_NAV, ...ACCOUNT_NAV]) {
      expect(group.icon, `icon of ${group.id}`).toBeTruthy();
      expect(isLeafGroup(group)).toBe(group.items.length === 1);
    }
    // Only groups with children become parents: the menu has both kinds.
    expect(ADMIN_NAV.some(isLeafGroup)).toBe(true);
    expect(ADMIN_NAV.some((g) => !isLeafGroup(g))).toBe(true);
  });

  it("every menu label exists in both languages", () => {
    for (const messages of [en, bn]) {
      const nav = messages.nav as unknown as { items: Record<string, string>; groups: Record<string, string> };
      for (const group of [...ADMIN_NAV, ...ACCOUNT_NAV]) {
        expect(nav.groups[group.label], `group ${group.label}`).toBeTruthy();
        for (const item of group.items) expect(nav.items[item.label], `item ${item.label}`).toBeTruthy();
      }
    }
  });
});
