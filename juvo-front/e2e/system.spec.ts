import { test, expect, type Page } from "@playwright/test";
import { loadState, sql } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { signInViaBff } from "./support/ui";
import {
  cleanSystem, deleteOutbox, seedFreshRows, markNotification, markOutbox, notificationStatusOf, outboxAttemptsOf, outboxStatusOf, seedManyDead, seedSystem, type SystemSeed,
} from "./support/system";

/**
 * Background jobs and integration health in the browser against the real API: job summary and runs with filters and a detail dialog,
 * the queue with state chips, one and bulk requeue (with the per-item result), the 100 selection limit, failed sends and their retry,
 * the health cards with the sandbox courier test, the status panel, and the permission cases. Queue messages, failed sends and job
 * runs are written into the throw-away database with a per-run tag (support/system.ts).
 */
let seed: SystemSeed;

test.beforeAll(() => {
  seed = seedSystem();
});
test.afterAll(() => {
  cleanSystem(seed);
});

async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const toast = (page: Page, text: string | RegExp) => expect(page.getByText(text).first()).toBeVisible();

async function openJobs(page: Page) {
  await page.goto("/admin/system/jobs");
  await expect(page.getByRole("heading", { level: 1, name: "Background jobs" })).toBeVisible();
}

async function openTab(page: Page, name: string) {
  await page.getByRole("tab", { name }).click();
}

/** Filters a list by its search box to this run's rows. */
async function searchFor(page: Page, label: string, text: string) {
  await page.getByLabel(label).fill(text);
}

test.describe("Background jobs: summary and runs", () => {
  test("a card per job shows last run, last success and failures in 24 h", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    const failing = page.locator(`[data-testid="job-card"][data-job="${seed.jobFailed}"]`);
    await expect(failing).toBeVisible();
    await expect(failing.getByTestId("job-failures")).toContainText("1 failed");
    await expect(failing).toContainText("Never"); // no success recorded for this job
    await expect(failing).toContainText("api_key=[hidden]"); // the API hides the secret in the last error
    await expect(failing).not.toContainText(seed.secret);
    const ok = page.locator(`[data-testid="job-card"][data-job="${seed.jobOk}"]`);
    await expect(ok.getByTestId("job-failures")).toHaveText("0");
    await expect(page.getByTestId("unrecorded-note")).toContainText("failed courier bookings");
    await expect(page.getByTestId("unrecorded-note")).toContainText("failed webhooks");
  });

  test("runs are filtered by search, outcome and reference and open in a detail dialog with a scrubbed error", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    await searchFor(page, "Search job runs", `e2esys${seed.tag}`);
    const rows = page.getByTestId("run-job").filter({ visible: true });
    await expect(rows).toHaveCount(2);

    await page.getByRole("combobox", { name: /^Outcome/ }).click();
    await page.getByRole("option", { name: "Failed" }).click();
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toHaveText(seed.jobFailed);

    await page.getByRole("combobox", { name: /^Outcome/ }).click();
    await page.getByRole("option", { name: "Any outcome" }).click();
    await expect(rows).toHaveCount(2);
    await page.getByLabel("Reference", { exact: true }).fill(seed.runReference);
    await expect(rows).toHaveCount(1);

    await page.getByTestId("run-details").filter({ visible: true }).first().click();
    const dialog = page.getByTestId("run-detail");
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(seed.jobFailed);
    await expect(dialog.getByTestId("run-correlation")).toHaveText(seed.runCorrelation);
    await expect(dialog).toContainText("Attempt");
    await expect(dialog).toContainText("api_key=[hidden]");
    await expect(dialog).not.toContainText(seed.secret);
    await expect(dialog.getByTestId("outcome-badge")).toHaveAttribute("data-outcome", "Failed");
    await page.keyboard.press("Escape");
    await expect(dialog).toHaveCount(0);
  });

  test("a filter with no result says so, and Clear filters brings the rows back", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    await searchFor(page, "Search job runs", `nothing-here-${seed.tag}`);
    await expect(page.getByTestId("run-job").filter({ visible: true })).toHaveCount(0);
    await page.getByRole("button", { name: "Clear filters" }).click();
    await expect(page.getByTestId("run-job").filter({ visible: true }).first()).toBeVisible();
  });
});

test.describe("Background jobs: the queue", () => {
  test("state chips show the counts and filter the queue; the payload is never shown", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    await openTab(page, "Queue");
    await expect(page.getByTestId("outbox-panel")).toBeVisible();
    await searchFor(page, "Search the queue", seed.typePrefix);
    const types = page.getByTestId("outbox-type").filter({ visible: true });
    await expect(types).toHaveCount(7); // 5 dead-lettered + 1 retrying + 1 pending

    expect(Number(await page.getByTestId("chip-count-DeadLettered").innerText())).toBeGreaterThanOrEqual(5);
    await page.getByTestId("chip-DeadLettered").click();
    await expect(page.getByTestId("chip-DeadLettered")).toHaveAttribute("aria-pressed", "true");
    await expect(types).toHaveCount(5);
    await expect(page.getByTestId("state-badge").filter({ visible: true }).first()).toHaveAttribute("data-state", "DeadLettered");
    await expect(page.getByTestId("outbox-panel")).not.toContainText("Handler failed: token=" + seed.secret);
    await expect(page.getByTestId("outbox-panel").getByText("token=[hidden]").first()).toBeVisible();

    await page.getByTestId("chip-Retrying").click();
    await expect(types).toHaveCount(1);
    await expect(types.first()).toHaveText(`${seed.typePrefix}.retry`);
    await expect(page.getByTestId("outbox-attempts").filter({ visible: true }).first()).toHaveText("2");

    await page.getByTestId("chip-Pending").click();
    await expect(types).toHaveCount(1);
    await expect(types.first()).toHaveText(`${seed.typePrefix}.pending`);
    await expect(page.getByTestId("outbox-requeue")).toHaveCount(0); // a fresh message has nothing to requeue
    await expect(page.getByText("Waiting for its first try").filter({ visible: true })).toHaveCount(1);

    await page.getByTestId("chip-all").click();
    await page.getByLabel("Message type").fill(`${seed.typePrefix}.dead`);
    await expect(types).toHaveCount(4);
  });

  test("one dead-lettered message is requeued after a confirm that says what happens", async ({ page }) => {
    expect(outboxStatusOf(`${seed.typePrefix}.single`)).toBe("2");
    await adminSignIn(page);
    await openJobs(page);
    await openTab(page, "Queue");
    await page.getByLabel("Message type").fill(`${seed.typePrefix}.single`);
    const rows = page.getByTestId("outbox-type").filter({ visible: true });
    await expect(rows).toHaveCount(1);
    await page.getByTestId("outbox-requeue").filter({ visible: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("attempts set to 0");
    await expect(dialog).toContainText("safe to repeat");
    await dialog.getByRole("button", { name: "Cancel" }).click();
    expect(outboxStatusOf(`${seed.typePrefix}.single`)).toBe("2"); // cancel changes nothing

    await page.getByTestId("outbox-requeue").filter({ visible: true }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Requeue" }).click();
    await toast(page, "The message is back in line.");
    expect(outboxStatusOf(`${seed.typePrefix}.single`)).not.toBe("2"); // Pending (or already handled by the worker)
    expect(Number(outboxAttemptsOf(`${seed.typePrefix}.single`))).toBeLessThan(5);
  });

  test("several messages are requeued at once and the result lists what was skipped and why", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    await openTab(page, "Queue");
    await page.getByLabel("Message type").fill(`${seed.typePrefix}.dead`);
    await page.getByTestId("chip-DeadLettered").click();
    const rows = page.getByTestId("outbox-type").filter({ visible: true });
    await expect(rows).toHaveCount(4);

    await page.getByTestId("bulk-select-page").click();
    await expect(page.getByTestId("bulk-count")).toContainText("4 of 100 selected");
    // Between ticking and confirming the world moves on: one message is delivered, one disappears.
    markOutbox(`${seed.typePrefix}.dead.b`, 1);
    deleteOutbox(`${seed.typePrefix}.dead.c`);

    await page.getByTestId("bulk-action").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("Requeue 4 messages?");
    await expect(dialog).toContainText("is skipped");
    await dialog.getByRole("button", { name: "Requeue" }).click();

    const result = page.getByTestId("outbox-result");
    await expect(result).toBeVisible();
    await expect(result.getByTestId("result-counts")).toContainText("2 items were put back in line");
    await expect(result.getByTestId("result-counts")).toContainText("2 items were skipped");
    await expect(result.getByTestId("result-skipped-row")).toHaveCount(2);
    await expect(result).toContainText("Not found.");
    await expect(result).toContainText("already delivered");
    await expect(page.getByTestId("bulk-count")).toContainText("0 of 100 selected");
    expect(outboxStatusOf(`${seed.typePrefix}.dead.a`)).not.toBe("2");
    expect(outboxStatusOf(`${seed.typePrefix}.dead.d`)).not.toBe("2");
    await result.getByRole("button", { name: "Dismiss" }).click();
    await expect(result).toHaveCount(0);
  });

  test("at most 100 rows can be selected; the limit is said plainly and Clear selection resets it", async ({ page }) => {
    test.setTimeout(150_000);
    const type = seedManyDead(seed.tag);
    try {
      await adminSignIn(page);
      await openJobs(page);
      await openTab(page, "Queue");
      await page.getByLabel("Message type").fill(type);
      await page.getByTestId("chip-DeadLettered").click();
      const types = page.getByTestId("outbox-type").filter({ visible: true });
      await expect(types).toHaveCount(10);
      for (let i = 1; i <= 10; i++) {
        await page.getByTestId("bulk-select-page").click();
        await expect(page.getByTestId("bulk-count")).toContainText(`${i * 10} of 100 selected`);
        await page.getByTestId("page-next").click();
        await expect(page.getByTestId("pagination-summary")).toContainText(`${i * 10 + 1}`);
      }
      await expect(page.getByTestId("bulk-count")).toContainText("100 of 100 selected");
      // On page 11 nothing more can be ticked.
      await expect(types).toHaveCount(5);
      await expect(page.getByTestId("outbox-select").filter({ visible: true }).first()).toBeDisabled();
      await page.getByTestId("bulk-select-page").click();
      await expect(page.getByTestId("bulk-limit")).toContainText("at most 100");
      await expect(page.getByTestId("bulk-count")).toContainText("100 of 100 selected");
      await page.getByTestId("bulk-clear").click();
      await expect(page.getByTestId("bulk-count")).toContainText("0 of 100 selected");
      await expect(page.getByTestId("outbox-select").filter({ visible: true }).first()).toBeEnabled();
    } finally {
      deleteOutboxLike(type);
    }
  });
});

function deleteOutboxLike(prefix: string) {
  sql(`DELETE FROM OutboxMessages WHERE Type LIKE N'${prefix}.%'`);
}

test.describe("Background jobs: failed sends", () => {
  test("failed sends show a masked recipient and no text; one is retried after a confirm", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    await openTab(page, "Failed sends");
    await page.getByLabel("Template", { exact: true }).fill(seed.templatePrefix);
    const rows = page.getByTestId("failed-template").filter({ visible: true });
    await expect(rows).toHaveCount(4);
    const panel = page.getByTestId("failed-panel");
    await expect(panel).not.toContainText("+8801700000001");
    await expect(panel).not.toContainText("someone.private@example.com");
    await expect(panel).not.toContainText("private text");
    await expect(page.getByTestId("failed-recipient").filter({ visible: true }).first()).toContainText("•");
    await expect(panel).toContainText("password=[hidden]");
    await expect(panel).not.toContainText(seed.secret);

    await page.getByRole("combobox", { name: /^Channel/ }).click();
    await page.getByRole("option", { name: "E-mail" }).click();
    await expect(rows).toHaveCount(1);
    await expect(rows.first()).toHaveText(`${seed.templatePrefix}.mail`);
    await page.getByTestId("failed-retry").filter({ visible: true }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("attempts set to 0");
    await expect(dialog).toContainText("not checked again");
    await dialog.getByRole("button", { name: "Retry" }).click();
    await toast(page, "The message was queued to send again.");
    expect(notificationStatusOf(`${seed.templatePrefix}.mail`)).not.toBe("2"); // Pending, or already Sent by the sandbox
  });

  test("several failed sends are retried together; one that is no longer failed is skipped with its reason", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    await openTab(page, "Failed sends");
    await page.getByLabel("Template", { exact: true }).fill(`${seed.templatePrefix}.sms`);
    const rows = page.getByTestId("failed-template").filter({ visible: true });
    await expect(rows).toHaveCount(3);
    await page.getByTestId("bulk-select-page").click();
    await expect(page.getByTestId("bulk-count")).toContainText("3 of 100 selected");
    markNotification(`${seed.templatePrefix}.sms2`, 1); // sent in the meantime

    await page.getByTestId("bulk-action").click();
    await expect(page.getByRole("dialog")).toContainText("Retry 3 sends?");
    await page.getByRole("dialog").getByRole("button", { name: "Retry" }).click();
    const result = page.getByTestId("failed-result");
    await expect(result).toBeVisible();
    await expect(result.getByTestId("result-counts")).toContainText("2 items were put back in line");
    await expect(result.getByTestId("result-skipped-row")).toHaveCount(1);
    await expect(result).toContainText("Not failed (already pending, sent or skipped).");
  });

  test("with nothing failed the list says so in words", async ({ page }) => {
    await adminSignIn(page);
    await openJobs(page);
    await openTab(page, "Failed sends");
    await page.getByLabel("Template", { exact: true }).fill(`${seed.templatePrefix}.nothing-like-this`);
    await expect(page.getByTestId("failed-template").filter({ visible: true })).toHaveCount(0);
    await expect(page.getByTestId("failed-panel").getByText("Clear filters")).toBeVisible();
  });
});

test.describe("System health", () => {
  test("every integration slot has a card with a status, mode and the honest no-calls note", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/system/health");
    await expect(page.getByRole("heading", { level: 1, name: "System health" })).toBeVisible();
    const cards = page.getByTestId("integration-card");
    await expect(cards.first()).toBeVisible();
    expect(await cards.count()).toBeGreaterThanOrEqual(8); // gateways, couriers, SMS, e-mail, WhatsApp
    await expect(page.getByTestId("memory-note")).toContainText("starts empty after a restart");

    const sandbox = page.locator('[data-testid="integration-card"][data-status="Sandbox"]');
    expect(await sandbox.count()).toBeGreaterThanOrEqual(2); // sandbox gateway and sandbox courier
    await expect(sandbox.first().getByTestId("status-badge")).toContainText("Sandbox");
    await expect(sandbox.first().getByTestId("integration-mode")).toHaveText("Sandbox");

    const whatsapp = page.locator('[data-testid="integration-card"][data-key="whatsapp"]');
    await expect(whatsapp).toHaveAttribute("data-status", "Disabled");
    await expect(whatsapp.getByTestId("status-badge")).toContainText("Disabled");
    await expect(whatsapp.getByTestId("test-unavailable")).toContainText("not available");
    await expect(whatsapp.getByTestId("integration-test")).toHaveCount(0);

    // A slot nobody has called never shows green and never says Healthy.
    await expect(page.locator('[data-testid="integration-card"][data-status="Healthy"]').filter({ has: page.getByTestId("no-calls") })).toHaveCount(0);
    await expect(page.locator('[data-testid="integration-card"] [data-testid="status-badge"][data-status="Unknown"]').first().or(page.getByTestId("no-calls").first())).toBeVisible();
  });

  test("the sandbox courier can be tested and the result is recorded in its card", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/system/health");
    const testable = page.getByTestId("integration-test");
    await expect(testable.first()).toBeVisible();
    const card = page.locator('[data-testid="integration-card"]').filter({ has: testable.first() }).first();
    const key = await card.getAttribute("data-key");
    expect(key).toMatch(/^courier:/);
    await testable.first().click();
    const result = card.getByTestId("integration-test-result");
    await expect(result).toBeVisible();
    await expect(result).toHaveAttribute("data-ok", "true");
    await expect(result).toContainText("Connection works");
    await expect(card.getByTestId("no-calls")).toHaveCount(0); // the test is recorded like any other call
    await expect(card.getByText("Last success")).toBeVisible();
    await expect(card.getByTestId("integration-failures")).toHaveText("0");
    await expect(card.getByTestId("status-badge")).toHaveAttribute("data-status", "Sandbox"); // sandbox stays sandbox, never green
  });

  test("the status panel shows the database, version, uptime, backlog, failed jobs and the per-process note", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/system/health");
    const panel = page.getByTestId("status-panel");
    await expect(panel).toBeVisible();
    await expect(page.getByTestId("status-scope-note")).toContainText("this server process only");
    await expect(page.getByTestId("status-scope-note")).toContainText("restarts");
    await expect(page.getByTestId("stat-database")).toContainText("Reachable");
    await expect(page.getByTestId("stat-version")).not.toHaveText("");
    await expect(page.getByTestId("stat-uptime")).toContainText(/\d+ (s|min|h|d)/);
    await expect(page.getByTestId("stat-backlog")).toContainText("dead-lettered");
    await expect(page.getByTestId("stat-failed-jobs")).toBeVisible();
    await expect(page.getByTestId("stat-slow")).toContainText("requests seen");
    await expect(panel.getByRole("heading", { name: "Slow routes" })).toBeVisible();
    await expect(panel.getByRole("heading", { name: "Recent errors" })).toBeVisible();
    await expect(page.getByTestId("errors-empty").or(page.getByTestId("errors-list"))).toBeVisible();
  });

  test("the slow routes table lists routes by template, never real paths, or says that none was slow", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/system/health");
    const names = page.getByTestId("route-name").filter({ visible: true });
    const none = page.getByText("No slow requests recorded").first();
    await expect(names.first().or(none)).toBeVisible();
    if ((await names.count()) > 0) {
      const text = (await names.allInnerTexts()).join(" ");
      expect(text).toContain("/api/v1/");
      expect(text).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}-/); // no guid: the API stores the route template only
    }
  });

  test("Refresh reloads the figures and the auto refresh note is shown", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/system/health");
    await expect(page.getByTestId("health-updated")).toContainText("Updated");
    await expect(page.getByText("Refreshes by itself every 30 seconds")).toBeVisible();
    const requests: string[] = [];
    page.on("request", (r) => r.url().includes("/api/bff/") && requests.push(r.url()));
    await page.getByTestId("health-refresh").click();
    await expect.poll(() => requests.some((u) => u.includes("system/status")) && requests.some((u) => u.includes("integrations/health"))).toBe(true);
    await expect(page.getByTestId("health-refresh")).toBeEnabled();
    await expect(page.getByTestId("health-updated")).toContainText("Updated");
  });

  test("the auto refresh runs by itself every 30 seconds and stops while the tab is hidden", async ({ page }) => {
    test.setTimeout(120_000);
    await page.clock.install();
    await adminSignIn(page);
    await page.goto("/admin/system/health");
    await expect(page.getByTestId("status-panel")).toBeVisible();
    await expect(page.getByTestId("stat-database")).toBeVisible();
    let calls = 0;
    page.on("request", (r) => r.url().includes("system/status") && calls++);
    await page.clock.runFor(31_000);
    await expect.poll(() => calls).toBeGreaterThanOrEqual(1);
    const seen = calls;
    // Hide the tab: the timer pauses (TanStack Query checks document visibility).
    await page.evaluate(() => {
      Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "hidden" });
      Object.defineProperty(document, "hidden", { configurable: true, get: () => true });
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await page.clock.runFor(95_000);
    await page.waitForTimeout(500);
    expect(calls).toBe(seen);
  });
});

test.describe("Permissions and menu", () => {
  test("view-only staff see everything but no requeue or retry buttons and no checkboxes", async ({ page }) => {
    seedFreshRows(seed, "viewonly");
    const staff = await staffWithPermissions(["system.jobs.view", "system.health.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await openJobs(page);
    await openTab(page, "Queue");
    await page.getByLabel("Message type").fill(`${seed.typePrefix}.dead`);
    await expect(page.getByTestId("outbox-type").filter({ visible: true }).first()).toBeVisible();
    await expect(page.getByTestId("outbox-requeue")).toHaveCount(0);
    await expect(page.getByTestId("outbox-select")).toHaveCount(0);
    await expect(page.getByTestId("bulk-bar")).toHaveCount(0);
    await openTab(page, "Failed sends");
    await page.getByLabel("Template", { exact: true }).fill(seed.templatePrefix);
    await expect(page.getByTestId("failed-template").filter({ visible: true }).first()).toBeVisible();
    await expect(page.getByTestId("failed-retry")).toHaveCount(0);
    await expect(page.getByTestId("failed-select")).toHaveCount(0);
    await expect(page.getByTestId("bulk-bar")).toHaveCount(0);
    await page.goto("/admin/system/health");
    await expect(page.getByTestId("integration-card").first()).toBeVisible();
  });

  test("with the retry permission the buttons are there", async ({ page }) => {
    seedFreshRows(seed, "retryperm");
    const staff = await staffWithPermissions(["system.jobs.view", "system.jobs.retry"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await openJobs(page);
    await openTab(page, "Queue");
    await page.getByLabel("Message type").fill(`${seed.typePrefix}.dead`);
    await expect(page.getByTestId("outbox-requeue").filter({ visible: true }).first()).toBeVisible();
    await expect(page.getByTestId("bulk-bar")).toBeVisible();
    // Jobs without the health permission: the health page is closed.
    await page.goto("/admin/system/health");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("without the view permissions the menu items and both pages are closed", async ({ page }) => {
    const staff = await staffWithPermissions(["catalog.product.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/system/jobs"]')).toHaveCount(0);
    await expect(page.locator('a[href="/admin/system/health"]')).toHaveCount(0);
    await page.goto("/admin/system/jobs");
    await expect(page).toHaveURL(/forbidden/);
    await page.goto("/admin/system/health");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("a staff member with only the health permission sees only the health item", async ({ page }) => {
    const staff = await staffWithPermissions(["system.health.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/system/health"]').first()).toBeAttached();
    await expect(page.locator('a[href="/admin/system/jobs"]')).toHaveCount(0);
    await page.goto("/admin/system/jobs");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("the menu opens both pages from the System group", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin");
    await page.getByRole("button", { name: "System", exact: true }).click();
    await page.getByRole("link", { name: "Background jobs", exact: true }).first().click();
    await expect(page).toHaveURL(/\/admin\/system\/jobs$/);
    await expect(page.getByRole("heading", { level: 1, name: "Background jobs" })).toBeVisible();
    const health = page.getByRole("link", { name: "System health", exact: true }).first();
    if (!(await health.isVisible())) await page.getByRole("button", { name: "System", exact: true }).click();
    await health.click();
    await expect(page).toHaveURL(/\/admin\/system\/health$/);
    await expect(page.getByRole("heading", { level: 1, name: "System health" })).toBeVisible();
  });
});
