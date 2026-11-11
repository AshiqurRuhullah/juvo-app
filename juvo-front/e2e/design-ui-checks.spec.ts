import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { SHOTS, axeViolations, horizontalOverflow, settle, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The menu states of the redesign, each checked like every other screen (no sideways scroll, 44 px targets, axe, no console errors) and
 * kept as a screenshot: the open tree on a desktop, the icon rail with a parent's list on a tablet, the drawer on a phone.
 */
async function checks(page: Page, name: string, width: number) {
  await settle(page);
  mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: resolve(SHOTS, `${name}.png`), animations: "disabled" });
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility:\n${violations.join("\n")}`).toEqual([]);
}

test("the open menu tree on a desktop", async ({ page, context }) => {
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 860 });
  await signInViaBff(context, loadState().admin.email, loadState().admin.password);
  await page.goto("/admin/products");
  const aside = page.getByRole("complementary", { name: "Navigation" });
  await expect(aside.getByRole("button", { name: "Catalog" })).toHaveAttribute("aria-expanded", "true"); // the current page's parent
  await aside.getByRole("button", { name: "Sales" }).click();
  await expect(aside.getByText("Social orders")).toBeVisible();
  await checks(page, "nav-tree-1280", 1280);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403)/.test(e))).toEqual([]);
});

test("the icon rail with a parent's list on a tablet", async ({ page, context }) => {
  await page.setViewportSize({ width: 820, height: 1000 });
  await signInViaBff(context, loadState().admin.email, loadState().admin.password);
  await page.goto("/admin");
  await page.getByRole("complementary", { name: "Navigation" }).getByRole("button", { name: "Catalog" }).click();
  await expect(page.getByRole("link", { name: "Products" })).toBeVisible();
  await page.waitForTimeout(400);
  await checks(page, "nav-rail-flyout-820", 820);
});

test("the menu drawer on a phone", async ({ page, context }) => {
  await page.setViewportSize({ width: 360, height: 740 });
  await signInViaBff(context, loadState().admin.email, loadState().admin.password);
  await page.goto("/admin/inventory");
  await page.getByRole("button", { name: "Open menu" }).click();
  const drawer = page.getByRole("dialog", { name: "Menu" });
  await expect(drawer.getByRole("button", { name: "Catalog" })).toHaveAttribute("aria-expanded", "true");
  await drawer.getByRole("button", { name: "Sales" }).click();
  await page.waitForTimeout(500);
  await checks(page, "nav-drawer-360", 360);
});
