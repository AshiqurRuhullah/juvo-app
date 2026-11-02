import { test as setup, expect } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, saveState, type Seeded } from "./support/env";

type Tokens = { accessToken: string };
type Created = { user: { id: string }; temporaryPassword: string };

/**
 * Prepares the throw-away database through the real API: a usable Super Admin (the seeded one must change its password), two
 * branches and staff with different roles per branch. The browser tests then sign in as these users.
 */
setup("prepare users and branches", async () => {
  // 1. The seeded Super Admin signs in with the seed password and must change it.
  const first = await api<Tokens>("POST", "/api/v1/auth/login", { identifier: E2E.adminEmail, password: E2E.seedPassword });
  expect(first.status).toBe(200);
  const changed = await api<Tokens>("POST", "/api/v1/auth/change-password", { currentPassword: E2E.seedPassword, newPassword: E2E.adminPassword }, first.json.accessToken);
  expect(changed.status).toBe(200);
  const admin = changed.json.accessToken;

  const adminUser = await api<{ items: { id: string }[] }>("GET", `/api/v1/ums/users?search=${E2E.adminEmail}`, undefined, admin);
  const adminId = adminUser.json.items[0]!.id;

  // 2. Branches: the seed has HO and ONL; add two shops.
  const branches = await api<{ items: { id: string; code: string }[] }>("GET", "/api/v1/ums/branches?pageSize=50", undefined, admin);
  const ho = branches.json.items.find((b) => b.code === "HO")!;
  const mk = async (code: string, name: string) => {
    const r = await api<{ id: string }>("POST", "/api/v1/ums/branches", { code, nameEn: name, type: "Shop" }, admin);
    expect(r.status).toBe(201);
    return { id: r.json.id, code, name };
  };
  const a = await mk("E2EA", "Shop A");
  const b = await mk("E2EB", "Shop B");

  const roles = await api<{ items: { id: string; name: string }[] }>("GET", "/api/v1/ums/roles?pageSize=50", undefined, admin);
  const role = (name: string) => roles.json.items.find((r) => r.name === name)!.id;

  // 3. Staff. Each is created with a temporary password, then moved to a permanent one like a real first sign-in.
  const password = "E2e-Staff#Pass1";
  const mkUser = async (email: string, name: string, assignments: { branchId: string; roleId: string }[]): Promise<Seeded> => {
    const created = await api<Created>("POST", "/api/v1/ums/users", { email, fullName: name, assignments }, admin);
    expect(created.status).toBe(201);
    const limited = await api<Tokens>("POST", "/api/v1/auth/login", { identifier: email, password: created.json.temporaryPassword });
    const done = await api("POST", "/api/v1/auth/change-password", { currentPassword: created.json.temporaryPassword, newPassword: password }, limited.json.accessToken);
    expect(done.status).toBe(200);
    return { email, password, id: created.json.user.id, name };
  };

  const orderManager = await mkUser("om@e2e.test", "Olivia Order", [{ branchId: ho.id, roleId: role("Order Manager") }]);
  const manager = await mkUser("manager@e2e.test", "Mina Manager", [{ branchId: ho.id, roleId: role("Manager") }]);
  const multi = await mkUser("multi@e2e.test", "Mahir Multi", [
    { branchId: a.id, roleId: role("Order Manager") },
    { branchId: b.id, roleId: role("Support") },
  ]);

  saveState({
    db: E2E.db!,
    admin: { email: E2E.adminEmail, password: E2E.adminPassword, id: adminId, name: "Juvo Super Admin" },
    orderManager,
    manager,
    multi,
    branches: { a, b, ho: { id: ho.id, code: "HO" } },
  });
});
