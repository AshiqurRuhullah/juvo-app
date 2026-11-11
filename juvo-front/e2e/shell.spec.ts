import { test, expect, type Locator, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { signInViaBff } from "./support/ui";

const state = () => loadState();

/** Layout in use at the current width (the same breakpoints as the CSS). */
async function layoutOf(page: Page): Promise<"desktop" | "tablet" | "phone"> {
  const width = page.viewportSize()!.width;
  return width >= 1024 ? "desktop" : width >= 768 ? "tablet" : "phone";
}

/** In the icon rail the label is visually hidden (1 px, clipped) but still read by screen readers. */
async function expectLabelHidden(label: Locator) {
  const box = await label.boundingBox();
  expect(box === null || box.width <= 1).toBe(true);
}

test.beforeEach(async ({ context, page }) => {
  await signInViaBff(context, state().admin.email, state().admin.password);
  await page.goto("/admin");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");
});

test("the menu adapts to the screen: sidebar, rail or drawer with a bottom bar", async ({ page }) => {
  const layout = await layoutOf(page);
  const aside = page.getByRole("complementary", { name: "Navigation" });
  const openMenu = page.getByRole("button", { name: "Open menu" });
  const bottomBar = page.getByRole("navigation", { name: "Quick navigation" });

  if (layout === "desktop") {
    await expect(aside).toBeVisible();
    expect((await aside.boundingBox())!.width).toBeGreaterThan(250);
    await expect(openMenu).toBeHidden();
    await expect(bottomBar).toBeHidden();
    await expect(aside.getByText("Dashboard")).toBeVisible(); // labels are shown
  } else if (layout === "tablet") {
    await expect(aside).toBeVisible();
    expect((await aside.boundingBox())!.width).toBeLessThan(100); // icon rail
    await expectLabelHidden(aside.getByText("Dashboard").first());
    await expect(bottomBar).toBeHidden();
    await openMenu.click();
    const drawer = page.getByRole("dialog", { name: "Menu" });
    await expect(drawer.getByRole("link", { name: "Dashboard" })).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toBeHidden();
  } else {
    await expect(aside).toBeHidden();
    await expect(bottomBar).toBeVisible();
    await expect(bottomBar.getByRole("link", { name: "Dashboard" })).toBeVisible();
    await openMenu.click();
    const drawer = page.getByRole("dialog", { name: "Menu" });
    await expect(drawer).toBeVisible();
    await expect(drawer.getByText("User management")).toBeVisible();
    // Tapping outside closes it.
    await page.mouse.click(page.viewportSize()!.width - 8, 200);
    await expect(drawer).toBeHidden();
    // "More" in the bottom bar opens it too.
    await bottomBar.getByRole("button", { name: "More" }).click();
    await expect(drawer).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(drawer).toBeHidden();
  }
});

test("on desktop the sidebar collapses to a rail and the choice is remembered", async ({ page }) => {
  test.skip((await layoutOf(page)) !== "desktop", "collapse is a desktop control");
  const aside = page.getByRole("complementary", { name: "Navigation" });

  await page.getByRole("button", { name: "Collapse menu" }).click();
  await expect.poll(async () => (await aside.boundingBox())!.width).toBeLessThan(100);
  await expectLabelHidden(aside.getByText("Dashboard").first());

  await page.reload();
  expect((await aside.boundingBox())!.width).toBeLessThan(100); // server rendered collapsed from the cookie

  await page.getByRole("button", { name: "Expand menu" }).click();
  await expect.poll(async () => (await aside.boundingBox())!.width).toBeGreaterThan(250);
});

test("the menu is a tree: parents open and close, the page you are on keeps its parent open, and the choice is remembered", async ({ page }) => {
  test.skip((await layoutOf(page)) !== "desktop", "the tree is the desktop sidebar (the drawer is covered above)");
  const aside = page.getByRole("complementary", { name: "Navigation" });

  const sales = aside.getByRole("button", { name: "Sales" });
  await expect(sales).toHaveAttribute("aria-expanded", "false");
  await expect(aside.getByText("Social orders")).toBeHidden(); // closed: the children are folded away

  await sales.click();
  await expect(sales).toHaveAttribute("aria-expanded", "true");
  await expect(aside.getByText("Social orders")).toBeVisible();

  await page.reload(); // remembered in the browser
  await expect(aside.getByRole("button", { name: "Sales" })).toHaveAttribute("aria-expanded", "true");

  // The parent of the current page is open and the page is marked as current.
  await page.goto("/admin/ums/users");
  await expect(aside.getByRole("button", { name: "User management" })).toHaveAttribute("aria-expanded", "true");
  await expect(aside.getByRole("link", { name: "Users" })).toHaveAttribute("aria-current", "page");

  // Keyboard: a parent is a button, Enter toggles it.
  const catalog = aside.getByRole("button", { name: "Catalog" });
  await catalog.focus();
  await page.keyboard.press("Enter");
  await expect(catalog).toHaveAttribute("aria-expanded", "true");
  await page.keyboard.press("Enter");
  await expect(catalog).toHaveAttribute("aria-expanded", "false");
});

test("on a tablet the icon rail opens a parent's children in a small list", async ({ page }) => {
  test.skip((await layoutOf(page)) !== "tablet", "the rail with flyouts is the tablet layout");
  const aside = page.getByRole("complementary", { name: "Navigation" });

  await aside.getByRole("button", { name: "Catalog" }).click();
  const list = page.getByRole("link", { name: "Products" });
  await expect(list).toBeVisible();
  await list.click();
  await expect(page).toHaveURL(/\/admin\/products$/);
});

test("the command palette opens with Ctrl+K and jumps to a page", async ({ page }) => {
  await page.keyboard.press("Control+k");
  const palette = page.getByRole("dialog", { name: "Search and jump to a page" });
  await expect(palette).toBeVisible();

  await palette.getByPlaceholder("Search pages...").fill("profile");
  await palette.getByRole("option", { name: /My profile and security/ }).click();

  await expect(page).toHaveURL(/\/admin\/profile$/);
});

test("language switch changes the whole page to Bengali and back", async ({ page }) => {
  const layout = await layoutOf(page);
  const userMenu = page.getByTestId("user-menu");

  const switchTo = async (name: string) => {
    if (layout === "phone") {
      await userMenu.click(); // on a phone the language choices live in the account menu
    } else {
      await page.getByRole("button", { name: /^(Language|ভাষা)$/ }).first().click();
    }
    await page.getByRole("menuitem", { name }).click();
  };

  await switchTo("বাংলা");
  await expect(page.locator("html")).toHaveAttribute("lang", "bn");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("স্বাগতম");

  await switchTo("English");
  await expect(page.locator("html")).toHaveAttribute("lang", "en");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Welcome");
});

test("the theme switch applies the dark tokens and survives a reload", async ({ page }) => {
  const layout = await layoutOf(page);
  if (layout === "phone") {
    await page.getByTestId("user-menu").click();
  } else {
    await page.getByRole("button", { name: "Theme" }).click();
  }
  await page.getByRole("menuitem", { name: "Dark" }).click();

  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(background).toBe("rgb(20, 14, 13)"); // --surface in the dark theme (#140E0D)

  await page.reload();
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
});

test("the notification centre shows an honest empty state", async ({ page }) => {
  await page.getByRole("button", { name: "Notifications" }).click();
  await expect(page.getByText("You are all caught up")).toBeVisible();
});

test("modules that are not built yet are marked, not linked", async ({ page }) => {
  const layout = await layoutOf(page);
  if (layout !== "desktop") await page.getByRole("button", { name: "Open menu" }).click();
  const scope = layout === "desktop" ? page.getByRole("complementary", { name: "Navigation" }) : page.getByRole("dialog", { name: "Menu" });

  await expect(scope.getByRole("link", { name: "Dashboard" })).toBeVisible();
  await expect(scope.locator('a[href="/admin/orders"]')).toHaveCount(0); // no dead link
  // Orders is a child of the Sales parent, which is closed until it is opened.
  const sales = scope.getByRole("button", { name: "Sales" });
  if ((await sales.getAttribute("aria-expanded")) !== "true") await sales.click();
  await expect(scope.getByText("Orders", { exact: true })).toBeVisible();
  await expect(scope.getByText("Soon").first()).toBeVisible();
});
