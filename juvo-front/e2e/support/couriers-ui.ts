import type { Page } from "@playwright/test";

/**
 * Like support/ui.ts `settle`, but never waits forever for web fonts: a font that is still loading after 8 seconds (the first desktop
 * paint of a cold build) is reported and the checks go on with the fallback font. The checks themselves are unchanged.
 */
export async function settleBounded(page: Page): Promise<string[]> {
  await page.waitForLoadState("networkidle", { timeout: 15_000 }).catch(() => undefined);
  return page.evaluate(async () => {
    const pending = () => [...document.fonts].filter((f) => f.status === "loading" || f.status === "unloaded").map((f) => `${f.family} ${f.weight} ${f.status}`);
    await Promise.race([document.fonts.ready, new Promise((resolve) => setTimeout(resolve, 8000))]);
    return pending();
  });
}
