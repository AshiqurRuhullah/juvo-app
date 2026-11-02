import { test, expect } from "@playwright/test";
import { api, latestOtp, loadState } from "./support/env";
import { cookieNames, signInViaBff, signInViaUi } from "./support/ui";

const state = () => loadState();

test.describe("staff sign-in", () => {
  test("a wrong password shows a friendly message and stays on the login page", async ({ page }) => {
    await signInViaUi(page, state().admin.email, "Wrong-Password-1!");

    await expect(page.locator("main").getByRole("alert")).toContainText("not correct");
    await expect(page).toHaveURL(/\/login/);
    await expect(page.getByText("Unauthorized")).toHaveCount(0); // no raw server text
  });

  test("signing in lands on the dashboard and the tokens are not readable by page scripts", async ({ page, context }) => {
    await signInViaUi(page, state().admin.email, state().admin.password);

    await expect(page).toHaveURL(/\/admin$/);
    await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome, Juvo Super Admin");

    const cookies = await context.cookies();
    const access = cookies.find((c) => c.name === "juvo_at")!;
    const refresh = cookies.find((c) => c.name === "juvo_rt")!;
    expect(access.httpOnly).toBe(true);
    expect(refresh.httpOnly).toBe(true);
    expect(refresh.path).toBe("/api/bff"); // the refresh token is never sent to page requests
    expect(access.sameSite).toBe("Lax");

    expect(await page.evaluate(() => document.cookie)).not.toContain("juvo_at");
    expect(await page.evaluate(() => document.cookie)).not.toContain("juvo_rt");
    const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }));
    expect(stored.toLowerCase()).not.toContain("token");
  });

  test("tokens never appear in the BFF's browser-facing responses", async ({ page, context }) => {
    const response = await context.request.post("/api/bff/auth/login", { data: { identifier: state().admin.email, password: state().admin.password } });
    const body = await response.text();
    expect(body).not.toContain("accessToken");
    expect(body).not.toContain("refreshToken");
    expect(body).not.toMatch(/eyJ[A-Za-z0-9_-]{10,}\./); // no JWT

    // The token-returning API endpoints cannot be reached through the generic proxy.
    for (const path of ["/api/v1/auth/login", "/api/v1/auth/refresh", "/api/v1/auth/change-password", "/api/v1/auth/otp/verify"]) {
      const blocked = await context.request.post(`/api/bff${path}`, { data: {} });
      expect(blocked.status()).toBe(404);
    }
    await page.goto("/admin");
    await expect(page).toHaveURL(/\/admin$/);
  });

  test("protected pages send visitors to the login page and bring them back afterwards", async ({ page }) => {
    await page.goto("/admin/profile");
    await expect(page).toHaveURL(/\/login\?next=%2Fadmin%2Fprofile/);

    await page.getByLabel("Email or user name").fill(state().admin.email);
    await page.locator("#password").fill(state().admin.password);
    await page.getByRole("button", { name: "Sign in" }).click();

    await expect(page).toHaveURL(/\/admin\/profile$/);
  });

  test("a ?next= pointing to another site is ignored", async ({ page }) => {
    await signInViaUi(page, state().admin.email, state().admin.password, "//evil.example/steal");

    await expect(page).toHaveURL(/\/admin$/);
    expect(new URL(page.url()).hostname).toBe("localhost");
  });

  test("signing out ends the session", async ({ page, context }) => {
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin");

    await page.getByTestId("user-menu").click();
    await page.getByRole("menuitem", { name: "Sign out" }).click();

    await expect(page).toHaveURL(/\/login\?reason=signed-out/);
    await expect(page.getByText("You have been signed out.")).toBeVisible();
    expect(await cookieNames(context)).not.toContain("juvo_rt");

    await page.goto("/admin");
    await expect(page).toHaveURL(/\/login/);
  });
});

test.describe("silent refresh", () => {
  test("an expired access cookie is renewed when the user navigates", async ({ page, context }) => {
    await signInViaBff(context, state().admin.email, state().admin.password);
    await context.clearCookies({ name: "juvo_at" }); // as if the 15-minute token had expired

    await page.goto("/admin/profile");

    await expect(page).toHaveURL(/\/admin\/profile$/);
    await expect(page.getByRole("heading", { level: 1 })).toContainText("My profile and security");
    expect(await cookieNames(context)).toContain("juvo_at");
  });

  test("parallel requests with an expired token share ONE refresh and the session survives", async ({ page, context }) => {
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin");
    await context.clearCookies({ name: "juvo_at" });

    // Six calls leave the browser at once with the same (old) refresh cookie. Without a single in-flight refresh the second one
    // would present a used refresh token and the API would cut the whole session.
    const statuses = await page.evaluate(async () => {
      const calls = Array.from({ length: 6 }, () => fetch("/api/bff/api/v1/auth/me").then((r) => r.status));
      return Promise.all(calls);
    });
    expect(statuses).toEqual([200, 200, 200, 200, 200, 200]);

    // The session is still alive afterwards.
    const after = await page.evaluate(async () => (await fetch("/api/bff/api/v1/auth/sessions")).status);
    expect(after).toBe(200);
    await page.reload();
    await expect(page).toHaveURL(/\/admin$/);
  });

  test("a changed role takes effect without signing in again", async ({ page, context }) => {
    const s = state();
    await signInViaBff(context, s.orderManager.email, s.orderManager.password);
    await page.goto("/admin");
    await expect(page.getByRole("navigation", { name: "Main menu" }).first()).toBeVisible();
    await expect(page.getByText("Tax and invoices")).toHaveCount(0); // an Order Manager has no tax permissions

    // The administrator also makes this user an Accountant.
    const admin = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: s.admin.email, password: s.admin.password });
    const token = admin.json.accessToken;
    const roles = await api<{ items: { id: string; name: string }[] }>("GET", "/api/v1/ums/roles?pageSize=50", undefined, token);
    const accountant = roles.json.items.find((r) => r.name === "Accountant")!.id;
    const added = await api<{ assignmentId: string }>("POST", `/api/v1/ums/users/${s.orderManager.id}/branch-roles`, { branchId: s.branches.ho.id, roleId: accountant }, token);
    expect(added.status).toBe(201);

    try {
      // The user's token is now stale; the page renews it behind the scenes and shows the new menu.
      await page.reload();
      await expect(page.getByText("Tax and invoices").first()).toBeVisible();
    } finally {
      await api("DELETE", `/api/v1/ums/users/${s.orderManager.id}/branch-roles/${added.json.assignmentId}`, undefined, token);
    }
  });
});

test.describe("first sign-in of a new account", () => {
  test("a temporary password must be replaced before anything else", async ({ page }) => {
    const s = state();
    const admin = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: s.admin.email, password: s.admin.password });
    const roles = await api<{ items: { id: string; name: string }[] }>("GET", "/api/v1/ums/roles?pageSize=50", undefined, admin.json.accessToken);
    const support = roles.json.items.find((r) => r.name === "Support")!.id;
    const email = `first-${Date.now()}@e2e.test`;
    const created = await api<{ temporaryPassword: string }>("POST", "/api/v1/ums/users", { email, fullName: "Fiona First", assignments: [{ branchId: s.branches.ho.id, roleId: support }] }, admin.json.accessToken);
    expect(created.status).toBe(201);

    await signInViaUi(page, email, created.json.temporaryPassword);
    await expect(page).toHaveURL(/\/first-login/);
    await expect(page.getByRole("heading", { level: 1 })).toContainText("Choose your own password");

    // The admin area is closed until the password is changed.
    await page.goto("/admin");
    await expect(page).toHaveURL(/\/first-login/);

    await page.locator("#currentPassword").fill(created.json.temporaryPassword);
    await page.locator("#newPassword").fill("short");
    await page.locator("#confirm").fill("short");
    await page.getByRole("button", { name: "Change password" }).click();
    await expect(page.getByText("Use at least 10 characters.")).toBeVisible();

    await page.locator("#newPassword").fill("Fiona-Chosen#2026");
    await page.locator("#confirm").fill("Fiona-Chosen#2026");
    await page.getByRole("button", { name: "Change password" }).click();

    await expect(page).toHaveURL(/\/admin$/);
    await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome, Fiona First");
  });
});

test.describe("customer sign-in with a mobile number", () => {
  test("mobile number plus the SMS code opens the account, and staff pages stay closed", async ({ page }) => {
    const mobile = "01712300001";
    await page.goto("/login");
    await page.getByRole("tab", { name: "Customer" }).click();
    await page.getByLabel("Mobile number").fill(mobile);
    await page.getByRole("button", { name: "Send code" }).click();
    await expect(page.getByText("We sent a code to +8801712300001.")).toBeVisible();

    await page.getByLabel("Code from the SMS").fill("000000");
    await page.getByRole("button", { name: "Verify" }).click();
    await expect(page.locator("main").getByRole("alert")).toContainText("not correct");

    await page.getByLabel("Code from the SMS").fill(latestOtp("+8801712300001"));
    await page.getByRole("button", { name: "Verify" }).click();

    await expect(page).toHaveURL(/\/account$/);
    await expect(page.getByTestId("account-mobile")).toHaveText("+8801712300001");

    await page.goto("/admin");
    await expect(page).toHaveURL(/\/forbidden/);
    await expect(page.getByRole("heading", { level: 1 })).toContainText("do not have access");
  });

  test("an invalid mobile number is explained", async ({ page }) => {
    await page.goto("/login?as=customer");
    await page.getByLabel("Mobile number").fill("12345678");
    await page.getByRole("button", { name: "Send code" }).click();
    await expect(page.locator("main").getByRole("alert")).toContainText("valid mobile number");
  });
});

test.describe("password reset", () => {
  test("asking for a reset link looks the same for every email", async ({ page }) => {
    await page.goto("/forgot-password");
    await page.getByLabel("Email").fill("nobody-here@e2e.test");
    await page.getByRole("button", { name: "Send reset link" }).click();

    await expect(page.getByText("Check your email")).toBeVisible();
  });

  test("the reset page explains an incomplete link", async ({ page }) => {
    await page.goto("/reset-password");
    await expect(page.getByText("This reset link is incomplete.")).toBeVisible();
  });
});
