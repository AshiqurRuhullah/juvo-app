import { expect, type BrowserContext, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";

export const SHOTS = resolve(__dirname, "../../docs/ui-checks");

/** Signs in through the BFF endpoint (sets the same httpOnly cookies as the login page), without drawing the page. */
export async function signInViaBff(context: BrowserContext, email: string, password: string) {
  const response = await context.request.post("/api/bff/auth/login", { data: { identifier: email, password } });
  expect(response.status(), await response.text()).toBe(200);
}

/** Signs in through the real login form. */
export async function signInViaUi(page: Page, email: string, password: string, next?: string) {
  await page.goto(next ? `/login?next=${encodeURIComponent(next)}` : "/login");
  await page.getByLabel("Email or user name").fill(email);
  await page.locator("#password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

export async function cookieNames(context: BrowserContext): Promise<string[]> {
  return (await context.cookies()).map((c) => c.name);
}

/** The page must never scroll sideways (SRS 3.5). Returns the offending width info when it does. */
export async function horizontalOverflow(page: Page) {
  return page.evaluate(() => {
    const root = document.documentElement;
    const overflow = root.scrollWidth - root.clientWidth;
    const wide: string[] = [];
    if (overflow > 1) {
      for (const el of Array.from(document.querySelectorAll<HTMLElement>("body *"))) {
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.right > root.clientWidth + 1 && getComputedStyle(el).position !== "fixed") {
          wide.push(`${el.tagName.toLowerCase()}${el.className ? "." + String(el.className).split(" ").slice(0, 3).join(".") : ""} right=${Math.round(r.right)}`);
          if (wide.length >= 5) break;
        }
      }
    }
    return { overflow, viewport: root.clientWidth, wide };
  });
}

/** Visible interactive elements smaller than 44 x 44 px (inline text links are exempt). */
export async function smallTapTargets(page: Page): Promise<string[]> {
  return page.evaluate(() => {
    const selector = "button, a[href], input:not([type=hidden]):not([type=checkbox]):not([type=radio]), select, textarea, [role=button], [role=tab], [role=combobox], [role=menuitem], [role=checkbox], [role=radio], [role=switch]";
    const issues: string[] = [];
    for (const el of Array.from(document.querySelectorAll<HTMLElement>(selector))) {
      const style = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0 || style.visibility === "hidden" || style.display === "none") continue;
      if (el.closest("[aria-hidden=true]") || el.closest("[inert]")) continue;
      if (el.tagName === "A" && style.display === "inline") continue; // a link inside a sentence
      if (el.getAttribute("aria-disabled") === "true") continue;
      if (el.className && String(el.className).includes("sr-only")) continue;
      // A checkbox, radio or switch is operated through its label: the label row is the tap target.
      const row = el.closest("label") ?? (el.id ? document.querySelector(`label[for="${el.id}"]`) : null);
      if (row && /^(checkbox|radio|switch)$/.test(el.getAttribute("role") ?? el.getAttribute("type") ?? "")) {
        const lr = row.getBoundingClientRect();
        if (lr.height >= 43.5) continue;
      }
      if (r.width < 43.5 || r.height < 43.5) {
        const label = (el.getAttribute("aria-label") || el.textContent || el.getAttribute("id") || "").trim().slice(0, 40);
        issues.push(`${el.tagName.toLowerCase()} "${label}" ${Math.round(r.width)}x${Math.round(r.height)}`);
      }
    }
    return issues;
  });
}

/** WCAG 2.0/2.1 A and AA rules with axe. */
export async function axeViolations(page: Page, exclude: string[] = []) {
  let builder = new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]);
  for (const selector of exclude) builder = builder.exclude(selector);
  const results = await builder.analyze();
  return results.violations.map((v) => `${v.id} (${v.impact}): ${v.nodes.length} node(s), e.g. ${v.nodes[0]?.target.join(" ")} :: ${(v.nodes[0]?.html ?? "").slice(0, 160)}`);
}

export async function shot(page: Page, name: string) {
  mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: resolve(SHOTS, `${name}.png`), fullPage: true, animations: "disabled" });
}

/** Waits until fonts and images are settled so screenshots are stable. */
export async function settle(page: Page) {
  await page.waitForLoadState("networkidle").catch(() => undefined);
  await page.evaluate(() => document.fonts.ready);
}
