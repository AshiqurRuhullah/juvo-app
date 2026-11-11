import { test, expect, type Locator, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { signInViaBff, signInViaUi } from "./support/ui";

/**
 * The Step 5 gate, driven through the real screens: an admin creates a role with a few permissions, creates a user, gives them a
 * branch and the role; that user then signs in and sees only what the role allows, in the branch they work in.
 */
const state = () => loadState();
const stamp = Date.now().toString(36);

async function pick(page: Page, trigger: Locator, option: string | RegExp) {
  await trigger.click();
  await page.getByRole("option", { name: option }).first().click();
}

test.describe("UMS screens", () => {
  test.describe.configure({ mode: "serial" });

  const roleName = `Branch Auditor ${stamp}`;
  const userEmail = `auditor-${stamp}@e2e.test`;
  const userName = `Alex Auditor ${stamp}`;
  let temporaryPassword = "";
  const chosenPassword = "Auditor-Chosen#2026";

  test("admin creates a role with a few permissions", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "the long forms are driven on desktop; phones are covered by the UI checks");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/ums/roles");
    await expect(page.getByRole("heading", { level: 1, name: "Roles" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Super Admin" }).first()).toBeVisible();

    await page.getByRole("link", { name: "New role" }).click();
    await expect(page).toHaveURL(/\/admin\/ums\/roles\/new$/);
    await page.locator("#role-name").fill(roleName);
    await page.getByLabel("Description").fill("Can see users and branches, nothing else.");

    await page.getByLabel("Search permissions").fill("ums.user.view");
    await page.locator('[id="pm-ums.user.view"]').click();
    await page.getByLabel("Search permissions").fill("ums.branch.view");
    await page.locator('[id="pm-ums.branch.view"]').click();
    await expect(page.getByText("2 of ", { exact: false }).first()).toBeVisible();

    await page.getByRole("button", { name: "Create role" }).click();
    await expect(page).toHaveURL(/\/admin\/ums\/roles\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: roleName })).toBeVisible();
    await expect(page.getByText("2 of ", { exact: false }).first()).toBeVisible();
  });

  test("the permission matrix selects a whole module and clears it again", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/ums/roles/new");

    const counter = page.getByText(/\d+ of \d+ selected/);
    await expect(counter).toContainText("0 of");
    await page.getByRole("checkbox", { name: "Select every permission in Users, roles and branches" }).click();
    await expect(counter).not.toContainText("0 of");
    await page.getByRole("checkbox", { name: "Select every permission in Users, roles and branches" }).click();
    await expect(counter).toContainText("0 of");
  });

  test("admin creates a user and gives them a branch and the role", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/ums/users");
    await expect(page.getByRole("heading", { level: 1, name: "Users" })).toBeVisible();

    await page.getByRole("button", { name: "New user" }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Full name").fill(userName);
    await dialog.getByLabel("Email").fill("not-an-email");
    await dialog.getByRole("button", { name: "Create user" }).click();
    await expect(dialog.getByText("Enter a valid email address.")).toBeVisible();

    await dialog.getByLabel("Email").fill(userEmail);
    await pick(page, dialog.locator("#cu-branch"), new RegExp(state().branches.a.code));
    await pick(page, dialog.locator("#cu-role"), roleName);
    await dialog.getByRole("button", { name: "Add" }).click();
    await expect(dialog.getByText(new RegExp(`${state().branches.a.code} · ${roleName}`))).toBeVisible();

    await dialog.getByRole("button", { name: "Create user" }).click();
    await expect(dialog.getByRole("heading", { name: `${userName} is ready` })).toBeVisible();
    temporaryPassword = (await dialog.getByTestId("secret-value").innerText()).trim();
    expect(temporaryPassword.length).toBeGreaterThanOrEqual(10);

    await dialog.getByRole("link", { name: "Open the user" }).click();
    await expect(page.getByRole("heading", { level: 1, name: userName })).toBeVisible();

    // The Access tab shows the role and what it adds up to.
    await page.getByRole("tab", { name: "Branches and roles" }).click();
    await expect(page.getByRole("list", { name: "Branches and roles" }).getByText(roleName)).toBeVisible();
    await expect(page.getByText("2 permissions")).toBeVisible();
    await expect(page.getByText("branch.view", { exact: true })).toBeVisible();
  });

  test("the new user sees only what the role allows, in their own branch", async ({ browser }) => {
    test.skip(!temporaryPassword, "needs the user from the previous step");
    const context = await browser.newContext();
    const page = await context.newPage();
    if (page.viewportSize()!.width < 1024) await page.setViewportSize({ width: 1280, height: 800 });

    await signInViaUi(page, userEmail, temporaryPassword);
    await expect(page).toHaveURL(/\/first-login/);
    await page.locator("#currentPassword").fill(temporaryPassword);
    await page.locator("#newPassword").fill(chosenPassword);
    await page.locator("#confirm").fill(chosenPassword);
    await page.getByRole("button", { name: "Change password" }).click();
    await expect(page).toHaveURL(/\/admin$/);

    // Menu: users and branches, nothing else of the administration group.
    const sidebar = page.getByRole("complementary", { name: "Navigation" });
    // (The links sit in the User management parent, which may be folded: they are attached, whether or not it is open.)
    await expect(sidebar.locator('a[href="/admin/ums/users"]')).toBeAttached();
    await expect(sidebar.locator('a[href="/admin/ums/branches"]')).toBeAttached();
    await expect(sidebar.locator('a[href="/admin/ums/roles"]')).toHaveCount(0);
    await expect(sidebar.locator('a[href="/admin/ums/audit"]')).toHaveCount(0);
    await expect(sidebar.locator('a[href="/admin/ums/sign-ins"]')).toHaveCount(0);

    // Pages they do not hold the permission for are closed.
    await page.goto("/admin/ums/roles");
    await expect(page).toHaveURL(/\/forbidden/);
    await page.goto("/admin/ums/audit");
    await expect(page).toHaveURL(/\/forbidden/);

    // Users: no create button, and people from other branches are not listed.
    await page.goto("/admin/ums/users");
    await expect(page.getByRole("heading", { level: 1, name: "Users" })).toBeVisible();
    await expect(page.getByRole("button", { name: "New user" })).toHaveCount(0);
    await expect(page.getByRole("link", { name: new RegExp(userName) }).first()).toBeVisible();
    await expect(page.getByText("Olivia Order")).toHaveCount(0); // works in the head office only

    // Branches: only their own, and no create button. Another branch answers "not found".
    await page.goto("/admin/ums/branches");
    await expect(page.getByRole("heading", { level: 1, name: "Branches" })).toBeVisible();
    await expect(page.getByRole("link", { name: state().branches.a.name }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: state().branches.b.name })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "New branch" })).toHaveCount(0);
    await page.goto(`/admin/ums/branches/${state().branches.b.id}`);
    await expect(page.getByRole("heading", { name: "Branch not found" }).first()).toBeVisible();

    await context.close();
  });

  test("admin deactivates and reactivates the user", async ({ page, context }) => {
    test.skip(!temporaryPassword || page.viewportSize()!.width < 1024, "needs the user; desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/ums/users");
    await page.getByLabel("Search users").fill(userEmail);
    await page.getByRole("link", { name: new RegExp(userName) }).first().click();
    await expect(page.getByRole("heading", { level: 1, name: userName })).toBeVisible();

    await page.getByRole("button", { name: "Account actions" }).click();
    await page.getByRole("menuitem", { name: "Deactivate account" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Deactivate" }).click();
    await expect(page.getByText("This account is inactive.")).toBeVisible();

    await page.getByRole("button", { name: "Account actions" }).click();
    await page.getByRole("menuitem", { name: "Activate account" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Activate" }).click();
    await expect(page.getByText("This account is inactive.")).toHaveCount(0);
  });

  test("a branch can be created with a pin on the map and then deactivated", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/ums/branches/new");

    const code = `MAP${stamp}`.toUpperCase().slice(0, 12);
    await page.getByLabel("Code").fill(code);
    await page.getByLabel("Name (English)").fill(`Map Shop ${stamp}`);
    await page.getByLabel("Latitude").fill("23.8103");
    await page.getByLabel("Longitude").fill("90.4125");

    const map = page.getByLabel("Map to choose the branch location");
    await expect(map).toBeVisible();
    await expect(map.locator(".juvo-pin")).toBeVisible(); // the pin follows the typed position

    // Clicking the map moves the pin and fills the fields.
    const box = (await map.boundingBox())!;
    await map.click({ position: { x: box.width * 0.7, y: box.height * 0.3 } });
    await expect(page.getByLabel("Latitude")).not.toHaveValue("23.8103");

    const [created] = await Promise.all([
      page.waitForResponse((r) => r.url().endsWith("/api/v1/ums/branches") && r.request().method() === "POST"),
      page.getByRole("button", { name: "Create branch" }).click(),
    ]);
    expect(created.status(), await created.text()).toBe(201);
    await expect(page).toHaveURL(/\/admin\/ums\/branches\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: `Map Shop ${stamp}` })).toBeVisible();

    await page.getByRole("button", { name: "Deactivate" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Deactivate" }).click();
    await expect(page.getByText("Inactive").first()).toBeVisible();
  });

  test("the audit log and sign-in history show what happened", async ({ page, context }) => {
    test.skip(!temporaryPassword || page.viewportSize()!.width < 1024, "needs the user; desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);

    await page.goto("/admin/ums/audit");
    await expect(page.getByRole("heading", { level: 1, name: "Audit log" })).toBeVisible();
    await page.getByLabel("Search the audit log").fill(roleName);
    await page.getByRole("button", { name: /Details of the .* change/ }).first().click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("heading", { name: /change$/ })).toBeVisible();
    await expect(dialog.getByRole("table")).toBeVisible();
    await dialog.getByRole("button", { name: "Close" }).first().click();

    await page.goto("/admin/ums/sign-ins");
    await expect(page.getByRole("heading", { level: 1, name: "Sign-in history" })).toBeVisible();
    await page.getByLabel("Search sign-ins").fill(userEmail);
    await expect(page.getByText("Signed in").first()).toBeVisible();
  });
});
