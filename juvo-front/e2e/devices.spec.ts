import { test, expect } from "@playwright/test";
import { loadState } from "./support/env";
import { horizontalOverflow, settle, shot, signInViaBff, smallTapTargets } from "./support/ui";

/** Real device emulation (touch, device pixel ratio, user agent): the key pages must fit and be tappable. */
const pages = [
  { name: "storefront", path: "/", admin: false },
  { name: "login", path: "/login", admin: false },
  { name: "admin-dashboard", path: "/admin", admin: true },
  { name: "admin-profile", path: "/admin/profile", admin: true },
  { name: "design-system", path: "/design", admin: false },
];

for (const target of pages) {
  test(`${target.name} fits the screen and every control can be tapped`, async ({ page, context }, info) => {
    if (target.admin) await signInViaBff(context, loadState().admin.email, loadState().admin.password);
    await page.goto(target.path);
    await settle(page);

    await shot(page, `device-${info.project.name}-${target.name}`);
    const overflow = await horizontalOverflow(page);
    expect(overflow.overflow, JSON.stringify(overflow)).toBeLessThanOrEqual(1);
    const small = await smallTapTargets(page);
    expect(small, small.join("\n")).toEqual([]);
  });
}

test("viewport meta and safe areas are set for phones", async ({ page }) => {
  await page.goto("/login");
  const meta = await page.locator('meta[name="viewport"]').getAttribute("content");
  expect(meta).toContain("width=device-width");
  expect(meta).toContain("viewport-fit=cover");
  const fontSize = await page.getByLabel("Email or user name").evaluate((el) => parseFloat(getComputedStyle(el).fontSize));
  expect(fontSize).toBeGreaterThanOrEqual(16); // iOS does not zoom into inputs of 16px or more
});
