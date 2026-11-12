import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { apiCheckout, createStaff, disableAllRules, staffToken } from "./support/orders";
import { sampleVariants } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Step 9-b notifications screens against the real stack: edit a template with a live preview and history, the delivery log filled
 * by a real order, channel switches, and the limited view of a support agent. Everything changed is put back at the end.
 */
let admin = "";
let support: { email: string; password: string };

type TemplateRow = { code: string; channel: string; language: string; subject: string | null; body: string; isActive: boolean };
type LogPage = { items: { id: string; template: string; recipient: string; body: string; status: string }[] };

test.beforeAll(async () => {
  admin = await staffToken("admin");
  await disableAllRules(admin);
  support = await createStaff(admin, "support@e2e.test", "Sam Support", "Support", loadState().branches.ho.id);
});

async function open(page: Page, who?: { email: string; password: string }) {
  const user = who ?? loadState().admin;
  await signInViaBff(page.context(), user.email, user.password);
  await page.goto("/admin/notifications");
}

test.describe("Templates", () => {
  test.describe.configure({ mode: "serial" });
  let original: TemplateRow;

  test.beforeAll(async () => {
    const all = await api<TemplateRow[]>("GET", "/api/v1/notifications/templates?code=order.placed", undefined, admin);
    original = all.json.find((t) => t.code === "order.placed" && t.channel === "Sms" && t.language === "en")!;
    expect(original).toBeTruthy();
  });

  test.afterAll(async () => {
    const back = await api("PUT", "/api/v1/notifications/templates/order.placed/Sms/en", { subject: original.subject, body: original.body, isActive: original.isActive }, admin);
    expect(back.status).toBe(200);
  });

  test("an SMS template is edited with a live preview, a wrong placeholder is refused and the history shows the new version", async ({ page }) => {
    await open(page);
    await expect(page.getByRole("tab", { name: "Templates" })).toBeVisible();
    const group = page.locator('[data-testid="template-group"][data-code="order.placed"]');
    await expect(group).toBeVisible();
    await group.getByRole("button", { name: "Edit order.placed, SMS, English" }).click();

    const dialog = page.getByRole("dialog");
    const body = dialog.getByLabel("Message text");
    await expect(body).toHaveValue(/OrderNumber/);

    await body.fill("E2E-MARK order {{OrderNumber}} total {{Total}}");
    const preview = dialog.getByTestId("template-preview-body");
    await expect(preview).toContainText("E2E-MARK order");
    await expect(preview).not.toContainText("{{");
    await expect(dialog.getByTestId("template-count")).toContainText("characters");

    // A placeholder this message does not have: warned in the preview and refused on save.
    await body.fill("E2E-MARK {{Bogus}}");
    await expect(dialog.getByText("Placeholders this message does not have")).toBeVisible();
    await dialog.getByTestId("template-save").click();
    await expect(dialog.getByTestId("template-error")).toContainText("placeholder this message does not have");

    // Click-to-insert puts a placeholder into the text.
    await body.fill("E2E-MARK order ");
    await dialog.getByRole("button", { name: "Insert {{OrderNumber}}" }).click();
    await expect(body).toHaveValue("E2E-MARK order {{OrderNumber}}");

    await dialog.getByTestId("template-save").click();
    await expect(page.getByText("Template saved as a new version.")).toBeVisible();
    await expect(dialog).toBeHidden();

    // The history lists the new version as the one in use and keeps the built-in text.
    await group.getByRole("button", { name: "Edit order.placed, SMS, English" }).click();
    const history = page.getByRole("dialog").getByTestId("template-history");
    await expect(history.locator("li").first()).toContainText("In use");
    await expect(history.locator("li").first()).toContainText(/Version \d+/);
    await expect(history.locator('li[data-version="0"]')).toContainText("Built-in text");
    await history.locator('li[data-version="0"]').getByRole("button", { name: "Show text" }).click();
    await expect(history.locator('li[data-version="0"]')).toContainText("we received your order");
    await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();
    const stored = await api<TemplateRow[]>("GET", "/api/v1/notifications/templates?code=order.placed", undefined, admin);
    expect(stored.json.find((t) => t.channel === "Sms" && t.language === "en")!.body).toBe("E2E-MARK order {{OrderNumber}}");
  });
});

test.describe("Delivery log", () => {
  test("an order's message is listed, the filters narrow it and a sent message has no resend", async ({ page, playwright }) => {
    const guest = await playwright.request.newContext({ baseURL: E2E.webUrl });
    const items = await sampleVariants(guest, 1);
    const placed = await apiCheckout(guest, { lines: [{ variantId: items[0]!.id, quantity: 1 }] });
    await guest.dispose();

    // The outbox worker queues and sends asynchronously: wait until the row is in the log.
    let recipient = "";
    await expect
      .poll(
        async () => {
          const logs = await api<LogPage>("GET", "/api/v1/notifications/logs?template=order.placed&PageSize=50", undefined, admin);
          const row = logs.json.items.find((l) => l.body.includes(placed.number));
          recipient = row?.recipient ?? "";
          return row?.status ?? "none";
        },
        { timeout: 30_000 },
      )
      .toBe("Sent");

    await open(page);
    await page.getByRole("tab", { name: "Delivery log" }).click();
    await page.getByLabel("Template code").fill("order.placed");
    await page.getByLabel("Recipient", { exact: true }).fill(recipient);
    const row = page.locator('[data-testid="log-template"]:visible', { hasText: "order.placed" }).first();
    await expect(row).toBeVisible();
    await expect(page.getByText("Sent", { exact: true }).first()).toBeVisible();
    await expect(page.getByTestId("log-resend")).toHaveCount(0);

    await page.getByRole("button", { name: /^Details of order.placed/ }).first().click();
    const detail = page.getByTestId("log-detail");
    await expect(detail.getByTestId("log-body")).toContainText(placed.number);
    await expect(detail.getByTestId("log-detail-resend")).toHaveCount(0);
    await page.getByRole("dialog").getByRole("button", { name: "Close" }).first().click();

    // A status that matches nothing empties the list.
    await page.getByRole("combobox", { name: /^Status/ }).click();
    await page.getByRole("option", { name: "Gave up" }).click();
    await expect(page.locator('[data-testid="log-template"]:visible')).toHaveCount(0);
  });
});

test.describe("Channels", () => {
  test("a channel switch is saved and survives a reload, then is put back", async ({ page }) => {
    const before = await api<{ whatsAppEnabled: boolean }>("GET", "/api/v1/notifications/channels", undefined, admin);
    await open(page);
    await page.getByRole("tab", { name: "Channels" }).click();
    const wa = page.getByRole("switch", { name: /WhatsApp/ });
    await expect(wa).toHaveAttribute("aria-checked", String(before.json.whatsAppEnabled));
    await wa.click();
    await page.getByTestId("channels-save").click();
    await expect(page.getByText("Channels saved.")).toBeVisible();
    await page.reload();
    await page.getByRole("tab", { name: "Channels" }).click();
    await expect(page.getByRole("switch", { name: /WhatsApp/ })).toHaveAttribute("aria-checked", String(!before.json.whatsAppEnabled));
    const restore = await api("PUT", "/api/v1/notifications/channels", { smsEnabled: null, emailEnabled: null, whatsAppEnabled: before.json.whatsAppEnabled, defaultLanguage: null }, admin);
    expect(restore.status).toBe(200);
  });
});

test.describe("Permissions", () => {
  test("a support agent sees the delivery log but no templates, channels or resend", async ({ page }) => {
    await open(page, support);
    await expect(page.getByRole("tab", { name: "Delivery log" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Templates" })).toHaveCount(0);
    await expect(page.getByRole("tab", { name: "Channels" })).toHaveCount(0);
    await expect(page.getByTestId("log-resend")).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Edit order.placed, SMS, English" })).toHaveCount(0);
  });
});
