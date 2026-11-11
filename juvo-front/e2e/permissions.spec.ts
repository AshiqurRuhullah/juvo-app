import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { signInViaBff } from "./support/ui";

const state = () => loadState();

/** The sidebar menu items the user can see (desktop layout). */
const sidebar = (page: Page) => page.getByRole("complementary", { name: "Navigation" });

async function menuText(page: Page) {
  // innerText applies the CSS uppercase of group headings, so compare in lower case.
  return (await sidebar(page).innerText()).replace(/\s+/g, " ").toLowerCase();
}

test.describe("the menu is built from permissions", () => {
  test("Super Admin sees every group", async ({ page, context }) => {
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin");

    const text = await menuText(page);
    for (const label of ["Sales", "Catalog", "Delivery", "Payments and accounts", "Marketing", "Content", "Reports and support", "Settings", "User management", "Users", "Roles", "Branches", "Audit log", "Settings"]) {
      expect(text, `missing ${label}`).toContain(label.toLowerCase());
    }
    await expect(page.getByText("You hold every permission")).toBeVisible();
  });

  test("an Order Manager sees sales and delivery but not settings, user management or tax", async ({ page, context }) => {
    await signInViaBff(context, state().orderManager.email, state().orderManager.password);
    await page.goto("/admin");

    const text = await menuText(page);
    expect(text).toContain("orders");
    expect(text).toContain("couriers");
    expect(text).toContain("dashboard");
    for (const hidden of ["User management", "Settings", "Roles", "Audit log", "Tax and invoices", "Promotions"]) {
      expect(text, `${hidden} must be hidden`).not.toContain(hidden.toLowerCase());
    }
    await expect(page.getByText("User management is not available to you.")).toBeVisible();
  });

  test("a Manager sees the catalog but neither settings nor role management", async ({ page, context }) => {
    await signInViaBff(context, state().manager.email, state().manager.password);
    await page.goto("/admin");

    const text = await menuText(page);
    expect(text).toContain("catalog");
    expect(text).toContain("users"); // ums.user.view
    expect(text).not.toContain("settings");
    expect(text).not.toContain("roles");
  });
});

test.describe("branch and role together", () => {
  test("the same person has different menus in different branches", async ({ page, context }) => {
    const s = state();
    await signInViaBff(context, s.multi.email, s.multi.password);
    await page.goto("/admin");

    // Two branches, none chosen yet: branch permissions do not apply.
    await expect(page.getByText("No branch selected")).toBeVisible();
    expect(await menuText(page)).not.toContain("social orders");
    const switcher = page.getByRole("button", { name: /^Branch:/ });
    await expect(switcher).toBeVisible();

    // Shop A: Order Manager (can create orders for walk-in and social customers).
    await switcher.click();
    await page.getByRole("menuitem", { name: new RegExp(s.branches.a.name) }).click();
    await expect(page.getByRole("button", { name: `Branch: ${s.branches.a.name}` })).toBeVisible();
    await expect.poll(() => menuText(page)).toContain("social orders");
    expect(await menuText(page)).toContain("orders");

    // Shop B: Support (can view orders but not create them).
    await page.getByRole("button", { name: /^Branch:/ }).click();
    await page.getByRole("menuitem", { name: new RegExp(s.branches.b.name) }).click();
    await expect(page.getByRole("button", { name: `Branch: ${s.branches.b.name}` })).toBeVisible();
    await expect.poll(() => menuText(page)).not.toContain("social orders");
    expect(await menuText(page)).toContain("orders");
    expect(await menuText(page)).toContain("support");
  });

  test("a branch the user does not hold is refused by the server even if the cookie says otherwise", async ({ page, context }) => {
    const s = state();
    await signInViaBff(context, s.orderManager.email, s.orderManager.password); // only HO
    await context.addCookies([{ name: "juvo_branch", value: s.branches.b.id, url: "http://localhost:3100" }]);

    await page.goto("/admin");

    // The page still works (the server falls back to no branch) and the API never honoured the forged branch.
    await expect(page).toHaveURL(/\/admin$/);
    const response = await page.evaluate(async () => (await fetch("/api/bff/api/v1/auth/me")).status);
    expect(response).toBe(403);
  });

  test("an administrator can work in All branches and in any single branch", async ({ page, context }) => {
    const s = state();
    await signInViaBff(context, s.admin.email, s.admin.password);
    await page.goto("/admin");

    await expect(page.getByRole("button", { name: /^Branch:/ })).toBeVisible();
    await page.getByRole("button", { name: /^Branch:/ }).click();
    await page.getByRole("menuitem", { name: new RegExp(s.branches.b.name) }).click();
    await expect(page.getByRole("button", { name: `Branch: ${s.branches.b.name}` })).toBeVisible();

    await page.getByRole("button", { name: /^Branch:/ }).click();
    await page.getByRole("menuitem", { name: "All branches" }).click();
    await expect(page.getByRole("button", { name: "Branch: All branches" })).toBeVisible();
  });
});
