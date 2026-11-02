import { test, expect, type Page } from "@playwright/test";
import { latestOtp, loadState } from "./support/env";
import { axeViolations, horizontalOverflow, settle, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * UI checks at 360, 768 and 1280 px: a screenshot of every page (saved in docs/ui-checks), no sideways scrolling, tap targets of
 * at least 44 px, no accessibility violations (axe, WCAG 2.1 A and AA) and no console errors.
 */
const WIDTHS = [360, 768, 1280] as const;
const HEIGHT = { 360: 740, 768: 1024, 1280: 800 } as const;

type Auth = "none" | "admin" | "customer";
type Target = { name: string; path: string; auth: Auth; prepare?: (page: Page) => Promise<void>; axeExclude?: string[] };

const targets: Target[] = [
  { name: "login-staff", path: "/login", auth: "none" },
  {
    name: "login-customer",
    path: "/login?as=customer",
    auth: "none",
  },
  { name: "forgot-password", path: "/forgot-password", auth: "none" },
  { name: "reset-password-invalid", path: "/reset-password", auth: "none" },
  { name: "storefront-home", path: "/", auth: "none" },
  { name: "design-system", path: "/design", auth: "none" },
  { name: "forbidden-403", path: "/forbidden", auth: "none" },
  { name: "unauthorized-401", path: "/unauthorized", auth: "none" },
  { name: "not-found-404", path: "/this-page-does-not-exist", auth: "none" },
  { name: "admin-dashboard", path: "/admin", auth: "admin" },
  { name: "admin-profile", path: "/admin/profile", auth: "admin" },
  { name: "account-home", path: "/account", auth: "customer" },
];

let customerCounter = 0;

async function signInCustomer(page: Page) {
  const mobile = `01712${String(400000 + ++customerCounter + (Date.now() % 1000) * 100).slice(0, 6)}`;
  await page.context().request.post("/api/bff/session/preferences", { data: { sidebar: "expanded" } });
  const requested = await page.context().request.post("/api/bff/api/v1/auth/otp/request", { data: { mobile, purpose: null } });
  expect(requested.status()).toBe(202);
  const code = latestOtp(`+88${mobile}`);
  const verified = await page.context().request.post("/api/bff/auth/otp/verify", { data: { mobile, code } });
  expect(verified.status()).toBe(200);
}

for (const width of WIDTHS) {
  test.describe(`at ${width}px`, () => {
    for (const target of targets) {
      test(`${target.name}`, async ({ page, context }) => {
        const errors: string[] = [];
        page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
        page.on("pageerror", (e) => errors.push(String(e)));

        await page.setViewportSize({ width, height: HEIGHT[width] });
        const state = loadState();
        if (target.auth === "admin") await signInViaBff(context, state.admin.email, state.admin.password);
        if (target.auth === "customer") await signInCustomer(page);

        await page.goto(target.path);
        if (target.name === "login-customer") await expect(page.getByLabel("Mobile number")).toBeVisible();
        await target.prepare?.(page);
        await settle(page);

        await shot(page, `${target.name}-${width}`);

        const overflow = await horizontalOverflow(page);
        expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);

        const small = await smallTapTargets(page);
        expect(small, `tap targets under 44px:\n${small.join("\n")}`).toEqual([]);

        const violations = await axeViolations(page, target.axeExclude);
        expect(violations, `accessibility:\n${violations.join("\n")}`).toEqual([]);

        // 404 and 401 pages are meant to produce an HTTP error response; everything else must be clean.
        const unexpected = errors.filter((e) => !/Failed to load resource.*(404|401|403)/.test(e));
        expect(unexpected, `console errors:\n${unexpected.join("\n")}`).toEqual([]);
      });
    }
  });
}

test("long Bengali text and long names do not break the layout at 360px", async ({ page, context }) => {
  await page.setViewportSize({ width: 360, height: 740 });
  await signInViaBff(context, loadState().admin.email, loadState().admin.password);
  await context.request.post("/api/bff/session/preferences", { data: { locale: "bn" } });
  await page.goto("/admin");
  await settle(page);

  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, JSON.stringify(overflow)).toBeLessThanOrEqual(1);
  await shot(page, "admin-dashboard-bengali-360");
  await context.request.post("/api/bff/session/preferences", { data: { locale: "en" } });
});

// Browser zoom (or a larger default font size) makes the CSS viewport narrower: 200% on a 1280 px screen is a 640 px viewport and
// 400% is 320 px. Media queries follow the viewport, so these widths are what a person who enlarges text actually gets.
for (const [zoom, width] of [[200, 640], [400, 320]] as const) {
  test(`the admin dashboard stays usable when text is enlarged to ${zoom}%`, async ({ page, context }) => {
    await page.setViewportSize({ width, height: 700 });
    await signInViaBff(context, loadState().admin.email, loadState().admin.password);
    await page.goto("/admin");
    await settle(page);

    const overflow = await horizontalOverflow(page);
    expect(overflow.overflow, JSON.stringify(overflow)).toBeLessThanOrEqual(1);
    await shot(page, `admin-dashboard-zoom-${zoom}pct`);
  });
}
