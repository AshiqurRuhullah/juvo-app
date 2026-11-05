import { test, expect } from "@playwright/test";
import { loadState } from "./support/env";
import { axeViolations, horizontalOverflow, settle, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The same checks as ui-checks.spec.ts (screenshot, no sideways scroll, 44 px tap targets, axe, no console errors) for every UMS
 * screen at 360, 768 and 1280 px. Detail pages need ids, which are looked up through the BFF.
 */
const WIDTHS = [360, 768, 1280] as const;
const HEIGHT = { 360: 740, 768: 1024, 1280: 800 } as const;

type Page = { name: string; path: (ids: Ids) => string; prepare?: (page: import("@playwright/test").Page) => Promise<void> };
type Ids = { user: string; role: string; superRole: string; branch: string };

const pages: Page[] = [
  { name: "ums-users", path: () => "/admin/ums/users" },
  { name: "ums-user-detail", path: (ids) => `/admin/ums/users/${ids.user}` },
  {
    name: "ums-user-access",
    path: (ids) => `/admin/ums/users/${ids.user}`,
    prepare: async (page) => {
      await page.getByRole("tab", { name: "Branches and roles" }).click();
      await expect(page.getByRole("heading", { name: "What this person can do" })).toBeVisible();
    },
  },
  {
    name: "ums-user-sessions",
    path: (ids) => `/admin/ums/users/${ids.user}`,
    prepare: async (page) => {
      await page.getByRole("tab", { name: "Sessions" }).click();
      await expect(page.getByRole("heading", { name: "Active sessions" })).toBeVisible();
    },
  },
  { name: "ums-roles", path: () => "/admin/ums/roles" },
  { name: "ums-role-new", path: () => "/admin/ums/roles/new" },
  {
    name: "ums-role-matrix-open",
    path: (ids) => `/admin/ums/roles/${ids.role}`,
    prepare: async (page) => {
      await page.getByRole("button", { name: "Expand all" }).click();
      await expect(page.getByRole("button", { name: /Orders/ }).first()).toHaveAttribute("aria-expanded", "true");
    },
  },
  { name: "ums-role-super", path: (ids) => `/admin/ums/roles/${ids.superRole}` },
  { name: "ums-branches", path: () => "/admin/ums/branches" },
  { name: "ums-branch-new", path: () => "/admin/ums/branches/new", prepare: async (page) => void (await expect(page.getByLabel("Map to choose the branch location")).toBeVisible()) },
  { name: "ums-branch-detail", path: (ids) => `/admin/ums/branches/${ids.branch}` },
  { name: "ums-audit", path: () => "/admin/ums/audit" },
  { name: "ums-sign-ins", path: () => "/admin/ums/sign-ins" },
];

async function lookupIds(request: import("@playwright/test").APIRequestContext): Promise<Ids> {
  const get = async <T>(path: string) => (await (await request.get(`/api/bff/api/v1/ums/${path}`)).json()) as T;
  const users = await get<{ items: { id: string; email: string }[] }>("users?PageSize=50");
  const roles = await get<{ items: { id: string; name: string; isSuperAdmin: boolean }[] }>("roles?PageSize=50");
  const branches = await get<{ items: { id: string }[] }>("branches?PageSize=50");
  const user = users.items.find((u) => u.email === loadState().orderManager.email) ?? users.items[0]!;
  return { user: user.id, role: roles.items.find((r) => !r.isSuperAdmin)!.id, superRole: roles.items.find((r) => r.isSuperAdmin)!.id, branch: branches.items[0]!.id };
}

for (const width of WIDTHS) {
  test.describe(`UMS at ${width}px`, () => {
    for (const target of pages) {
      test(target.name, async ({ page, context }) => {
        const errors: string[] = [];
        page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
        page.on("pageerror", (e) => errors.push(String(e)));

        await page.setViewportSize({ width, height: HEIGHT[width] });
        await signInViaBff(context, loadState().admin.email, loadState().admin.password);
        const ids = await lookupIds(context.request);

        await page.goto(target.path(ids));
        await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
        await expect(page.getByText("Loading").first()).toHaveCount(0);
        await target.prepare?.(page);
        await settle(page);
        // Lists and forms load their data after the page: wait until no skeleton is left.
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0, { timeout: 15_000 });

        await shot(page, `${target.name}-${width}`);

        const overflow = await horizontalOverflow(page);
        expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);

        const small = await smallTapTargets(page);
        expect(small, `tap targets under 44px:\n${small.join("\n")}`).toEqual([]);

        const violations = await axeViolations(page);
        expect(violations, `accessibility:\n${violations.join("\n")}`).toEqual([]);

        const unexpected = errors.filter((e) => !/Failed to load resource.*(404|401|403)/.test(e) && !/tile\.openstreetmap\.org/.test(e));
        expect(unexpected, `console errors:\n${unexpected.join("\n")}`).toEqual([]);
      });
    }
  });
}
