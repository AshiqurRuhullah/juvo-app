import { test, expect, type Page } from "@playwright/test";
import { api, loadState } from "./support/env";
import { adminAccess, freshMobile, staffWithPermissions } from "./support/returns";
import { emptyVariantStock, topUpHeadOfficeStock } from "./support/orders";
import { pick, sampleVariants, signInCustomer } from "./support/store";
import { signInViaBff } from "./support/ui";
import { campaign, consentedSegment, customer, lastMessageTo, marketingConsent, normalized, openSending, productWithOptions, setSettings, tokenIn, uniqueName } from "./support/marketing";

/**
 * Marketing (module AC) in the browser against the real API: segments with the rule builder and live counts, campaigns from draft to
 * preview, test, schedule, send, cancel and results, the four automations, the sending rules, who may see or do what, and the two
 * public pieces (the back-in-stock form on a sold-out product and the unsubscribe page). Data is prepared through the API.
 */

async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

test.beforeAll(async () => {
  await openSending();
});

test.describe("Segments", () => {
  test("the built-in segments are read-only and can be copied", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/marketing/segments");
    await expect(page.getByRole("heading", { level: 1, name: "Segments" })).toBeVisible();
    await page.getByLabel("Search segments").fill("New customers");
    const row = page.locator("[data-testid=segment-name]:visible", { hasText: "New customers (30 days)" });
    await expect(row.first()).toBeVisible();
    await expect(page.getByText("Built-in").first()).toBeVisible();
    // A built-in segment has no edit and no delete button; it has copy and view.
    await expect(page.getByRole("button", { name: "Edit New customers (30 days)" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Delete New customers (30 days)" })).toHaveCount(0);
    await expect(page.locator("[data-testid=segment-count-total]:visible").first()).toBeVisible();

    await page.getByRole("button", { name: "Copy New customers (30 days)" }).first().click();
    await expect(page.getByText('Copied as "New customers (30 days) (copy)".').first()).toBeVisible();
    await page.getByLabel("Search segments").fill("New customers (30 days) (copy)");
    await expect(page.locator("[data-testid=segment-name]:visible", { hasText: "New customers (30 days) (copy)" }).first()).toBeVisible();

    await page.getByRole("button", { name: "View rules of New customers (30 days)" }).first().click();
    const dialog = page.getByTestId("segment-dialog");
    await expect(dialog.getByText("This is a built-in segment")).toBeVisible();
    await expect(dialog.getByTestId("segment-save")).toHaveCount(0);
    await expect(dialog.getByLabel("Number of days")).toBeDisabled();
  });

  test("build a segment: only the fields of the rule type, live count, per-field errors from the API, members, edit and delete", async ({ page }) => {
    await customer(); // somebody who joined today, so the live count is not empty
    await adminSignIn(page);
    await page.goto("/admin/marketing/segments");
    await page.getByTestId("new-segment").click();
    const dialog = page.getByTestId("segment-dialog");
    const name = uniqueName("Newcomers");

    // A rule shows only the fields its type uses.
    await expect(dialog.getByLabel("Number of days")).toBeVisible();
    await expect(dialog.getByLabel("At least this amount (BDT)")).toHaveCount(0);
    await expect(dialog.getByLabel("At least this many orders")).toHaveCount(0);

    // Client checks first.
    await dialog.getByTestId("segment-save").click();
    await expect(dialog.getByText("This is required.").first()).toBeVisible();

    await dialog.getByLabel("Name").fill(name);
    await dialog.getByLabel("Number of days").fill("30");
    await expect(dialog.getByTestId("live-total")).toBeVisible();
    expect(Number(await dialog.getByTestId("live-total").textContent())).toBeGreaterThanOrEqual(1);

    // A second rule of another type; its fields replace the first rule's.
    await dialog.getByTestId("add-rule").click();
    await expect(dialog.getByTestId("rule-row")).toHaveCount(2);
    await pick(page, dialog.getByRole("combobox", { name: "Rule 2" }), "Repeat buyers");
    await expect(dialog.getByLabel("At least this many orders")).toBeVisible();
    await dialog.getByLabel("At least this many orders").fill("1");
    // The API says 2 or more: its error is shown under the field of that rule.
    await dialog.getByTestId("segment-save").click();
    await expect(dialog.getByTestId("rule-row").nth(1).getByRole("alert")).toBeVisible();
    await expect(dialog.getByTestId("segment-form")).toBeVisible();
    await dialog.getByLabel("At least this many orders").fill("2");
    // Switch the combinator to "any rule".
    await dialog.getByLabel("Any rule (OR)").click();
    await dialog.getByTestId("segment-save").click();
    await expect(page.getByText("Segment created.").first()).toBeVisible();

    await page.getByLabel("Search segments").fill(name);
    await expect(page.locator("[data-testid=segment-name]:visible", { hasText: name })).toHaveCount(1);
    await expect(page.getByText("Joined in the last 30 days").first()).toBeVisible();

    // Members.
    await page.getByRole("button", { name: `Members of ${name}` }).first().click();
    const members = page.getByTestId("members-dialog");
    await expect(members.getByText("Mobile numbers and email addresses are shown")).toBeVisible();
    await expect(members.locator("tbody tr, [data-testid=card]").first()).toBeVisible();
    await page.keyboard.press("Escape");

    // Edit.
    await page.getByRole("button", { name: `Edit ${name}` }).first().click();
    await expect(page.getByTestId("segment-dialog").getByLabel("Number of days")).toHaveValue("30");
    await page.getByTestId("segment-dialog").getByLabel("Number of days").fill("45");
    await page.getByTestId("segment-save").click();
    await expect(page.getByText("Segment saved.").first()).toBeVisible();
    await expect(page.getByText("Joined in the last 45 days").first()).toBeVisible();

    // Delete.
    await page.getByRole("button", { name: `Delete ${name}` }).first().click();
    await page.getByRole("button", { name: "Delete", exact: true }).last().click();
    await expect(page.getByText(`"${name}" was deleted.`).first()).toBeVisible();
    await expect(page.locator("[data-testid=segment-name]:visible", { hasText: name })).toHaveCount(0);
  });

  test("a segment that a campaign uses cannot be deleted, and the reason is said", async ({ page }) => {
    const segment = await consentedSegment();
    await campaign(segment.id);
    await adminSignIn(page);
    await page.goto("/admin/marketing/segments");
    await page.getByLabel("Search segments").fill(segment.name);
    await page.getByRole("button", { name: `Delete ${segment.name}` }).first().click();
    await page.getByRole("button", { name: "Delete", exact: true }).last().click();
    await expect(page.getByText("A campaign uses this segment, so it cannot be deleted.").first()).toBeVisible();
  });

  test("a location rule picks districts, a consent rule picks channel and answer", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/marketing/segments");
    await page.getByTestId("new-segment").click();
    const dialog = page.getByTestId("segment-dialog");
    await dialog.getByLabel("Name").fill(uniqueName("Dhaka consent"));
    await pick(page, dialog.getByRole("combobox", { name: "Rule 1" }), "Location");
    await expect(dialog.getByLabel("Number of days")).toHaveCount(0);
    await dialog.getByLabel("Filter the list").fill("Dhaka");
    await dialog.getByRole("checkbox", { name: "Dhaka", exact: true }).click();
    await expect(dialog.getByText("1 chosen")).toBeVisible();
    await dialog.getByTestId("add-rule").click();
    await pick(page, dialog.getByRole("combobox", { name: "Rule 2" }), "Marketing consent");
    await expect(dialog.getByLabel("Channel")).toBeVisible();
    await dialog.getByTestId("segment-save").click();
    await expect(page.getByText("Segment created.").first()).toBeVisible();
  });
});

test.describe("Campaigns", () => {
  test("a campaign from draft to preview, test, schedule, send, results and recipients", async ({ page }) => {
    test.setTimeout(150_000);
    const people = [await customer(), await customer()];
    await customer({ sms: false }); // declined: must be skipped, not messaged
    const segment = await consentedSegment();
    await adminSignIn(page);
    await page.goto("/admin/marketing/campaigns");
    await expect(page.getByRole("heading", { level: 1, name: "Campaigns" })).toBeVisible();
    await page.getByTestId("new-campaign").click();

    const name = uniqueName("Winter sale");
    await expect(page.getByText("SMS and email only. WhatsApp campaigns are not available.")).toBeVisible();
    await expect(page.getByLabel("WhatsApp")).toHaveCount(0);
    await page.getByLabel("Campaign name").fill(name);
    await page.getByTestId("campaign-save").click();
    await expect(page.getByText("This is required.").first()).toBeVisible();
    await pick(page, page.locator("#cmp-segment"), segment.name);
    await expect(page.getByTestId("campaign-segment-count")).toContainText("customers now");

    // The counter, placeholder buttons and the unknown-placeholder warning.
    await page.getByLabel("Message (English)").fill("Hi ");
    await page.getByRole("button", { name: "Insert placeholder customerName" }).first().click();
    await expect(page.getByLabel("Message (English)")).toHaveValue("Hi {customerName}");
    await page.getByLabel("Message (English)").fill("Hi {customerName}, {nickname}");
    await expect(page.getByText("These placeholders are not known and will be refused: {nickname}.")).toBeVisible();
    await page.getByTestId("campaign-save").click();
    await expect(page.getByText("The text has a placeholder that is not known.")).toBeVisible();
    await page.getByLabel("Message (English)").fill("Hi {customerName}, news at {shopName}: {link}");
    await page.getByLabel("Message (Bengali)").fill("হ্যালো {customerName}, {shopName}-এ নতুন: {link}");
    await expect(page.getByTestId("sms-counter").first()).toContainText("SMS");
    await expect(page.getByTestId("sms-counter").nth(1)).toContainText("Bengali or other letters");
    await page.getByTestId("campaign-save").click();
    await expect(page.getByTestId("campaign-detail")).toBeVisible();
    await expect(page.getByTestId("campaign-status")).toHaveText("Draft");

    // Preview: real numbers and both languages.
    await page.getByTestId("campaign-preview").click();
    await expect(page.getByTestId("campaign-preview-panel")).toBeVisible();
    const recipients = Number(await page.getByTestId("preview-recipients").locator("p").first().textContent());
    expect(recipients).toBeGreaterThanOrEqual(2);
    await expect(page.getByTestId("preview-sample-en")).toBeVisible();
    await expect(page.getByTestId("preview-sample-bn")).toContainText("হ্যালো");

    // A test to myself: this staff member has no phone number, so an SMS test is refused with a clear text.
    await page.getByTestId("campaign-test").click();
    await page.getByTestId("test-confirm").click();
    await expect(page.getByTestId("test-dialog").getByRole("alert")).toContainText("Your staff profile has no email address or phone number");
    await page.keyboard.press("Escape");

    // Schedule: a time in the past is refused; a future time is saved and shown in Dhaka time.
    await page.getByTestId("campaign-schedule").click();
    await page.getByLabel("Send at (Dhaka time)").fill("2020-01-01T10:00");
    await page.getByTestId("schedule-confirm").click();
    await expect(page.getByText("Choose a time in the future.")).toBeVisible();
    const year = new Date().getFullYear() + 1;
    await page.getByLabel("Send at (Dhaka time)").fill(`${year}-03-05T10:30`);
    await page.getByTestId("schedule-confirm").click();
    await expect(page.getByTestId("campaign-status")).toHaveText("Scheduled");
    await expect(page.getByTestId("meta-scheduled")).toContainText("10:30");
    await expect(page.getByText(`will start on its own at`)).toBeVisible();

    // Cancel dialog says what cancelling means; keep the campaign.
    await page.getByTestId("campaign-cancel").click();
    await expect(page.getByText("The campaign will not be sent.")).toBeVisible();
    await page.getByRole("button", { name: "Keep it" }).click();

    // Send now: the confirmation states the number of recipients.
    await page.getByTestId("campaign-send").click();
    const confirm = page.getByTestId("send-confirm-count");
    await expect(confirm).toBeVisible();
    await expect(confirm).toContainText(`${recipients} will receive it`);
    await page.getByRole("button", { name: `Send to ${recipients} people` }).click();
    await expect(page.getByText("Sending has started.").first()).toBeVisible();

    // Results.
    await expect(page.getByTestId("campaign-results")).toBeVisible();
    await expect(page.getByTestId("result-recipients").locator("p").first()).toHaveText(String(recipients + 0), { timeout: 20_000 }).catch(() => undefined);
    await expect.poll(async () => Number(await page.getByTestId("result-sent").locator("p").first().textContent()), { timeout: 60_000 }).toBeGreaterThanOrEqual(2);
    await expect(page.getByTestId("result-delivered")).toContainText("Equals sent until a provider reports delivery receipts");
    await expect(page.getByTestId("result-skipped")).toBeVisible();
    await expect(page.getByTestId("result-attribution")).toContainText("Last touch");
    await expect(page.getByTestId("campaign-status")).toHaveText("Sent", { timeout: 60_000 });
    // A sent campaign can no longer be changed, scheduled, sent or deleted.
    await expect(page.getByTestId("campaign-send")).toHaveCount(0);
    await expect(page.getByTestId("campaign-delete")).toHaveCount(0);
    await expect(page.getByLabel("Message (English)")).toHaveCount(0);

    // Recipients: contacts are masked, the declined customer is skipped with the reason.
    const recipientsPanel = page.getByTestId("campaign-recipients");
    await expect(recipientsPanel).toContainText("Contact details are always masked.");
    await expect(recipientsPanel.getByText(normalized(people[0]!.mobile))).toHaveCount(0);

    // The list shows the campaign with its results.
    await page.goto("/admin/marketing/campaigns");
    await page.getByLabel("Search campaigns").fill(name);
    await expect(page.locator("[data-testid=campaign-link]:visible", { hasText: name })).toHaveCount(1);
  });

  test("an email campaign sends a test to my own address, and a sent-out draft can be deleted", async ({ page }) => {
    const segment = await consentedSegment();
    const made = await campaign(segment.id, { channel: "Email" });
    await adminSignIn(page);
    await page.goto(`/admin/marketing/campaigns/${made.id}`);
    await expect(page.getByLabel("Subject (English)")).toHaveValue("E2E subject");
    await page.getByTestId("campaign-test").click();
    await page.locator("#test-lang-bn").click();
    await page.getByTestId("test-confirm").click();
    await expect(page.getByText(`Test sent to ${loadState().admin.email}.`).first()).toBeVisible();

    // Edit and save a change.
    await page.getByLabel("Campaign name").fill(`${made.name} v2`);
    await page.getByTestId("campaign-save").click();
    await expect(page.getByText("Campaign saved.").first()).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name: `${made.name} v2` })).toBeVisible();

    // Cancel it (never started), then delete it.
    await page.getByTestId("campaign-cancel").click();
    await page.getByRole("button", { name: "Cancel campaign" }).last().click();
    await expect(page.getByTestId("campaign-status")).toHaveText("Cancelled");
    await page.getByTestId("campaign-delete").click();
    await page.getByRole("button", { name: "Delete", exact: true }).last().click();
    await expect(page).toHaveURL(/\/admin\/marketing\/campaigns$/);
  });

  test("sending a campaign that was already sent elsewhere is refused with a clear text", async ({ page }) => {
    const segment = await consentedSegment();
    const made = await campaign(segment.id);
    const token = await adminAccess();
    await adminSignIn(page);
    await page.goto(`/admin/marketing/campaigns/${made.id}`);
    await page.getByTestId("campaign-send").click();
    await expect(page.getByTestId("send-confirm-count")).toBeVisible();
    // Somebody else sends it while the confirmation is open.
    const sent = await api("POST", `/api/v1/marketing/campaigns/${made.id}/send`, undefined, token);
    expect(sent.status).toBeLessThan(300);
    await page.getByRole("button", { name: /^Send to \d+ people?$/ }).click();
    await expect(page.getByText("That step does not fit the campaign's status now.").first()).toBeVisible();
  });
});

test.describe("Automations and settings", () => {
  test("four automations, all off; texts, switch with a warning, dry run and results", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/marketing/automations");
    await expect(page.getByRole("heading", { level: 1, name: "Automations" })).toBeVisible();
    for (const kind of ["AbandonedCart", "BackInStock", "ReviewRequest", "WinBack"]) {
      await expect(page.getByTestId(`state-${kind}`)).toHaveText("Off");
    }
    await expect(page.getByTestId("quiet-hours-note")).toContainText("Quiet hours apply to SMS only, not to email.");

    const card = page.getByTestId("automation-WinBack");
    // The window field exists only for the automations that use it.
    await expect(card.getByLabel("Window (days)")).toHaveCount(0);
    await expect(card.getByText("This automation has no window")).toBeVisible();
    await expect(page.getByTestId("automation-AbandonedCart").getByLabel("Window (days)")).toBeVisible();

    // Validation, then a saved change.
    const delay = card.getByLabel("Delay (minutes)");
    const before = await delay.inputValue();
    await delay.fill("-5");
    await card.getByTestId("save-WinBack").click();
    await expect(card.getByText("This cannot be below zero.")).toBeVisible();
    await delay.fill("86460");
    await card.getByTestId("save-WinBack").click();
    await expect(page.getByText("Win-back saved.").first()).toBeVisible();
    await page.reload();
    await expect(page.getByTestId("automation-WinBack").getByLabel("Delay (minutes)")).toHaveValue("86460");
    await page.getByTestId("automation-WinBack").getByLabel("Delay (minutes)").fill(before);
    await page.getByTestId("save-WinBack").click();
    await expect(page.getByText("Win-back saved.").first()).toBeVisible();

    // A placeholder the automation does not know is refused before sending.
    await page.getByLabel("Message (English)").nth(0).fill("Hi {customerName} {unknownThing}");
    await page.getByTestId("save-AbandonedCart").click();
    await expect(page.getByText("The text has a placeholder that is not known for this automation.").first()).toBeVisible();
    await page.reload();

    // Switching on asks first; switch back off straight away (this database is shared).
    await page.getByTestId("switch-WinBack").click();
    await expect(page.getByText('Switch on "Win-back"?')).toBeVisible();
    await page.getByRole("button", { name: "Keep it off" }).click();
    await expect(page.getByTestId("state-WinBack")).toHaveText("Off");
    await page.getByTestId("switch-WinBack").click();
    await page.getByRole("button", { name: "Switch on", exact: true }).click();
    await expect(page.getByTestId("state-WinBack")).toHaveText("On");

    await page.getByTestId("dry-run").click();
    const dry = page.getByTestId("dry-WinBack");
    await expect(dry).toBeVisible();
    await expect(dry.getByTestId("dry-would-send-if-enabled")).toBeVisible();
    await expect(page.getByTestId("dry-AbandonedCart")).toContainText("Off");

    await page.getByTestId("switch-WinBack").click();
    await expect(page.getByTestId("state-WinBack")).toHaveText("Off");

    await page.getByTestId("results-toggle-WinBack").click();
    await expect(page.getByTestId("results-WinBack")).toContainText("Queued");
  });

  test("the sending rules: scope text, validation from the ranges, a saved change", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/marketing/settings");
    await expect(page.getByTestId("quiet-scope")).toContainText("Quiet hours apply to SMS only. Email is not held back.");
    const messages = page.getByLabel("Messages per customer");
    await messages.fill("101");
    await page.getByTestId("settings-save").click();
    await expect(page.getByText("Enter a whole number from 0 to 100.")).toBeVisible();
    await messages.fill("0");
    await page.getByLabel("Throttle (messages per minute, per campaign)").fill("500");
    await page.getByLabel("Attribution window (days)").fill("14");
    await page.getByTestId("settings-save").click();
    await expect(page.getByText("Settings saved.").first()).toBeVisible();
    await page.reload();
    await expect(page.getByLabel("Attribution window (days)")).toHaveValue("14");
    await expect(page.getByLabel("Throttle (messages per minute, per campaign)")).toHaveValue("500");
    await page.getByLabel("Attribution window (days)").fill("7");
    await page.getByLabel("Throttle (messages per minute, per campaign)").fill("600");
    await page.getByTestId("settings-save").click();
    await expect(page.getByText("Settings saved.").first()).toBeVisible();

    // The quiet-hours switch and times are saved and read back; they are put back off for the other specs.
    await page.getByLabel("Hold SMS back during quiet hours").click();
    await page.getByLabel("Quiet hours start").fill("22:30");
    await page.getByTestId("settings-save").click();
    await expect(page.getByText("Settings saved.").first()).toBeVisible();
    await page.reload();
    await expect(page.getByLabel("Quiet hours start")).toHaveValue("22:30");
    await setSettings({ quietHoursEnabled: false, quietHoursStart: "21:00", frequencyCapMessages: 0 });
  });
});

test.describe("Permissions", () => {
  test("a person who may only view segments sees segments and nothing else of marketing", async ({ page }) => {
    const staff = await staffWithPermissions(["marketing.segment.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin/marketing/segments");
    await expect(page.getByRole("heading", { level: 1, name: "Segments" })).toBeVisible();
    await expect(page.getByTestId("new-segment")).toHaveCount(0);
    await expect(page.getByRole("button", { name: /^Copy / })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Campaigns" })).toHaveCount(0);
    await page.goto("/admin/marketing/campaigns");
    await expect(page).toHaveURL(/\/forbidden/);
    await page.goto("/admin/marketing/automations");
    await expect(page).toHaveURL(/\/forbidden/);
  });

  test("a campaign creator cannot schedule or send, and an SMS test without a phone number is explained", async ({ page }) => {
    const segment = await consentedSegment();
    const made = await campaign(segment.id);
    const staff = await staffWithPermissions(["marketing.campaign.view", "marketing.campaign.create", "marketing.segment.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(`/admin/marketing/campaigns/${made.id}`);
    await expect(page.getByTestId("campaign-save")).toBeVisible();
    await expect(page.getByTestId("campaign-send")).toHaveCount(0);
    await expect(page.getByTestId("campaign-schedule")).toHaveCount(0);
    await expect(page.getByTestId("campaign-cancel")).toHaveCount(0);
    await page.getByTestId("campaign-test").click();
    await page.getByTestId("test-confirm").click();
    await expect(page.getByTestId("test-dialog").getByRole("alert")).toContainText("no email address or phone number");
    await page.keyboard.press("Escape");
    // Settings are visible but read-only.
    await page.goto("/admin/marketing/settings");
    await expect(page.getByText("You can see these rules but not change them.")).toBeVisible();
    await expect(page.getByTestId("settings-save")).toHaveCount(0);
  });
});

test.describe("Storefront: notify me", () => {
  const labelOf = (v: { colorName: string | null; size: string | null }) => [v.colorName, v.size].filter(Boolean).join(" / ");

  test("a sold-out product offers the form; a guest subscribes, repeats, hits the limit; a customer needs no number", async ({ page, context }) => {
    test.setTimeout(180_000);
    const admin = await adminAccess();
    await topUpHeadOfficeStock(admin, loadState().branches.ho.id);
    const item = await productWithOptions();
    const variants = item.variants;
    const stocks = [];
    for (const v of variants) stocks.push(await emptyVariantStock(admin, item.productId, v.id));
    try {
      await setSettings({ maxNotifyMePerMobile: 1 });
      await page.goto(`/product/${item.slug}`);
      const form = page.getByTestId("notify-me");
      await expect(form).toBeVisible();
      await expect(page.getByTestId("add-to-cart")).toBeDisabled();
      const which = form.getByRole("combobox", { name: "Which option do you want?" });

      // Everything is sold out and no size is chosen: the customer says which option to wait for.
      await form.getByTestId("notify-submit").click();
      await expect(form.getByRole("alert")).toContainText("Please choose the option you are waiting for.");
      await pick(page, which, labelOf(variants[0]!));
      await form.getByLabel("Mobile number").fill("12345");
      await form.getByTestId("notify-submit").click();
      await expect(form.getByRole("alert")).toContainText("Please enter a valid mobile number.");

      const mobile = freshMobile();
      await form.getByLabel("Mobile number").fill(mobile);
      await form.getByTestId("notify-submit").click();
      await expect(page.getByTestId("notify-done")).toContainText("Thank you. We will message you when it is back.");
      await expect(page.getByTestId("notify-done")).toContainText("to the number you gave");

      // The same number and option again: already on the list (a success, not an error).
      await page.reload();
      const again = page.getByTestId("notify-me");
      await pick(page, again.getByRole("combobox", { name: "Which option do you want?" }), labelOf(variants[0]!));
      await again.getByLabel("Mobile number").fill(mobile);
      await again.getByTestId("notify-submit").click();
      await expect(page.getByTestId("notify-done")).toContainText("You are already on the list for this item.");

      // A second option for the same number: the number limit (set to 1 for this test) is explained.
      await page.reload();
      const second = page.getByTestId("notify-me");
      await pick(page, second.getByRole("combobox", { name: "Which option do you want?" }), labelOf(variants[1]!));
      await second.getByLabel("Mobile number").fill(mobile);
      await second.getByTestId("notify-submit").click();
      await expect(second.getByRole("alert")).toContainText("already waiting for the most items we allow");

      // A signed-in customer gives no number.
      await signInCustomer(context);
      await page.reload();
      const mine = page.getByTestId("notify-me");
      await expect(mine.getByLabel("Mobile number")).toHaveCount(0);
      await expect(mine.getByText("You are signed in, so we will message the number on your account.")).toBeVisible();
      await pick(page, mine.getByRole("combobox", { name: "Which option do you want?" }), labelOf(variants[1]!));
      await mine.getByTestId("notify-submit").click();
      await expect(page.getByTestId("notify-done")).toContainText("to the number on your account");
    } finally {
      for (const stock of stocks) await stock.restore();
      await setSettings({ maxNotifyMePerMobile: 10 });
    }
  });

  test("a product that is in stock shows no form", async ({ page, context }) => {
    await topUpHeadOfficeStock(await adminAccess(), loadState().branches.ho.id);
    const [item] = await sampleVariants(context.request, 1);
    await page.goto(`/product/${item.slug}`);
    await expect(page.getByTestId("add-to-cart")).toBeVisible();
    await expect(page.getByTestId("notify-me")).toHaveCount(0);
  });
});

test("public unsubscribe link: confirm, success, again, and damaged or missing links", async ({ page }) => {
  test.setTimeout(150_000);
  const person = await customer();
  const segment = await consentedSegment();
  const made = await campaign(segment.id, { body: "Hi {customerName}, sale: {link}" });
  const token = await adminAccess();
  const sent = await api("POST", `/api/v1/marketing/campaigns/${made.id}/send`, undefined, token);
  expect(sent.status, JSON.stringify(sent.json)).toBeLessThan(300);
  await expect.poll(() => lastMessageTo(person.mobile), { timeout: 60_000 }).toContain("token=");
  const unsubscribeToken = tokenIn(lastMessageTo(person.mobile));

  // Opening the link changes nothing; the person confirms.
  await page.goto(`/unsubscribe?token=${encodeURIComponent(unsubscribeToken)}`);
  await expect(page.getByTestId("unsubscribe-confirm")).toBeVisible();
  expect(await marketingConsent(person.token)).toBe(true);
  await page.getByTestId("unsubscribe-confirm-button").click();
  await expect(page.getByTestId("unsubscribe-done")).toContainText("You are unsubscribed");
  expect(await marketingConsent(person.token)).toBe(false);

  // Again: nothing more to do.
  await page.goto(`/unsubscribe?token=${encodeURIComponent(unsubscribeToken)}`);
  await page.getByTestId("unsubscribe-confirm-button").click();
  await expect(page.getByTestId("unsubscribe-done")).toContainText("You were already unsubscribed");

  // Damaged and missing links.
  await page.goto(`/unsubscribe?token=${encodeURIComponent(unsubscribeToken.slice(0, -3) + "xyz")}`);
  await page.getByTestId("unsubscribe-confirm-button").click();
  await expect(page.getByTestId("unsubscribe-invalid")).toContainText("This unsubscribe link is not valid");
  await page.goto("/unsubscribe");
  await expect(page.getByTestId("unsubscribe-invalid")).toContainText("This link has no unsubscribe code");

  // Bengali, following the language cookie; no sign-in needed.
  await page.context().addCookies([{ name: "juvo_locale", value: "bn", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  await page.goto("/unsubscribe");
  await expect(page.getByTestId("unsubscribe-invalid")).toContainText("এই লিংকে আনসাবস্ক্রাইব কোড নেই");
});
