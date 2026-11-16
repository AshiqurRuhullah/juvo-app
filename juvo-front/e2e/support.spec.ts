import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState, sql } from "./support/env";
import { makePng } from "./support/png";
import { adminAccess, deliveredOrder, freshMobile, staffWithPermissions, stockedVariants } from "./support/returns";
import { pick, signInCustomer } from "./support/store";
import { bdDay, canned, customerTicket, findTicket, reportOf, staffStep, staffTicket, summary, uniqueText } from "./support/tickets";
import { signInViaBff } from "./support/ui";

/**
 * Support tickets (module Y) in the browser against the real API: the guest contact form, the customer's requests (with an order and
 * a picture), staff replies and internal notes, the status flow, saved replies in both languages, bulk steps, the summary and the report,
 * and who may see or do what. Data is prepared through the API (the e2e database starts empty).
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const status = (page: Page) => page.getByTestId("ticket-status").first();

test.describe("Support: guest contact form", () => {
  test("a guest sends the contact form, sees the reference and staff find the ticket", async ({ page }) => {
    await page.goto("/contact");
    await expect(page.getByRole("heading", { level: 1, name: "Contact us" })).toBeVisible();
    await page.getByTestId("contact-submit").click();
    await expect(page.getByText("This is required.").first()).toBeVisible();

    const subject = uniqueText("Where is my parcel");
    const mobile = freshMobile();
    await page.getByLabel("Your name").fill("Nadia Rahman");
    await page.getByLabel("Mobile number").fill(mobile);
    await page.getByLabel("Email (optional)").fill("nadia@example.com");
    await page.getByLabel("Topic").selectOption("delivery");
    await page.getByLabel("Subject").fill(subject);
    await page.getByLabel("Your message").fill("It was due yesterday.");
    await page.getByTestId("contact-submit").click();

    await expect(page.getByTestId("contact-done")).toBeVisible();
    await expect(page.getByText("Our answer comes by SMS or email.")).toBeVisible();
    const number = (await page.getByTestId("contact-number").textContent())!.trim();
    expect(number).toMatch(/^TK-\d{4}-\d{6}$/);

    const found = await findTicket(subject);
    expect(found.number).toBe(number);
    await adminSignIn(page);
    await page.goto("/admin/support");
    await page.getByLabel("Search tickets").fill(subject);
    await expect(page.locator("[data-testid=ticket-link]:visible")).toHaveCount(1);
    await expect(page.locator("[data-testid=ticket-link]:visible")).toContainText(number);
  });

  test("a signed-in customer is sent to the account request page instead", async ({ page, context }) => {
    await signInCustomer(context);
    await page.goto("/contact");
    await expect(page).toHaveURL(/\/account\/support\/new/);
    await expect(page.getByRole("heading", { level: 1, name: "New request" })).toBeVisible();
  });

  test("the hourly limit per mobile number is explained", async ({ page }) => {
    const mobile = freshMobile();
    const guest = (n: number) => api("POST", "/api/v1/store/contact", { name: "Rate", mobile, email: null, subject: `Limit ${n}`, message: "x", category: "other", orderNumber: null });
    for (let i = 1; i <= 5; i++) expect((await guest(i)).status).toBe(201);
    await page.goto("/contact");
    await page.getByLabel("Your name").fill("Rate");
    await page.getByLabel("Mobile number").fill(mobile);
    await page.getByLabel("Subject").fill("One too many");
    await page.getByLabel("Your message").fill("Please?");
    await page.getByTestId("contact-submit").click();
    await expect(page.getByText("You have sent too many messages. Please wait a little and try again.")).toBeVisible();
    await expect(page.getByTestId("contact-done")).toHaveCount(0);
  });
});

test.describe("Support: customer", () => {
  test("a customer asks for help with an order, attaches a picture and sees the ticket", async ({ page, context }) => {
    const [variant] = await stockedVariants(1, 1, 3);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }]);
    await signInCustomer(context, order.mobile);

    await page.goto(`/account/orders/${order.orderId}`);
    await page.getByTestId("order-help-link").click();
    await expect(page).toHaveURL(new RegExp(`/account/support/new\\?order=${order.orderId}`));
    await expect(page.locator("#mt-order")).toContainText(order.number);

    await page.getByTestId("my-ticket-submit").click();
    await expect(page.getByText("This is required.").first()).toBeVisible();

    const subject = uniqueText("Zip is stuck");
    await page.getByLabel("Subject").fill(subject);
    await page.getByLabel("Your message").fill("The zip does not move.");
    await page.locator("#mt-file").setInputFiles({ name: "zip.png", mimeType: "image/png", buffer: makePng(1200, 900) });
    await expect(page.getByTestId("mt-list")).toContainText("zip.png");
    // Pressing send twice quickly files one ticket only.
    await page.getByTestId("my-ticket-submit").dblclick();
    await expect(page).toHaveURL(/\/account\/support\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: /^TK-/ })).toBeVisible();
    await expect(page.getByTestId("message-customer")).toContainText("The zip does not move.");
    await expect(page.getByRole("link", { name: "Open zip.png" })).toBeVisible();
    await expect(page.getByText(`Order ${order.number}`).first()).toBeVisible();

    await page.goto("/account/support");
    await expect(page.getByTestId("my-ticket")).toHaveCount(1);
    await expect(page.getByTestId("my-ticket").first()).toContainText(subject);

    const staff = await findTicket(subject);
    const detail = await api<{ orderNumber: string; messages: { attachments: unknown[] }[] }>("GET", `/api/v1/support/tickets/${staff.id}`, undefined, await adminAccess());
    expect(detail.json.orderNumber).toBe(order.number);
    expect(detail.json.messages[0]!.attachments).toHaveLength(1);
  });

  test("a customer cannot open another customer's ticket", async ({ page, context }) => {
    const owner = freshMobile();
    await signInCustomer(context, owner);
    const mine = await customerTicket(owner);
    const stranger = await context.browser()!.newContext({ baseURL: E2E.webUrl });
    const other = await stranger.newPage();
    await signInCustomer(stranger, freshMobile());
    await other.goto(`/account/support/${mine.id}`);
    await expect(other.getByText("We could not find that request.").first()).toBeVisible();
    await expect(other.getByText(mine.subject)).toHaveCount(0);
    await stranger.close();
    await page.goto(`/account/support/${mine.id}`);
    await expect(page.getByRole("heading", { level: 1, name: mine.number })).toBeVisible();
  });

  test("a customer replies, closes the request and reopens it inside the window", async ({ page, context }) => {
    const mobile = freshMobile();
    await signInCustomer(context, mobile);
    const ticket = await customerTicket(mobile);
    await page.goto(`/account/support/${ticket.id}`);
    await page.getByLabel("Your message").fill("One more detail.");
    await page.getByTestId("my-reply-send").click();
    await expect(page.getByTestId("message-customer")).toHaveCount(2);

    await page.getByTestId("my-close").click();
    await page.getByRole("dialog").getByRole("button", { name: "Close request" }).click();
    await expect(page.getByText("Your request is closed.").first()).toBeVisible();
    await expect(status(page)).toHaveText("Closed");
    await expect(page.getByTestId("my-reply-form")).toHaveCount(0);

    await page.getByTestId("my-reopen").click();
    await expect(status(page)).toHaveText("Open");
    await expect(page.getByTestId("my-reply-form")).toBeVisible();
  });
});

test.describe("Support: staff conversation", () => {
  test("staff reply publicly and add an internal note: the customer sees the reply and never the note", async ({ page, context, browser }) => {
    const mobile = freshMobile();
    await signInCustomer(context, mobile);
    const ticket = await customerTicket(mobile, { message: "Is my size available?" });
    const staffContext = await browser.newContext({ baseURL: E2E.webUrl });
    const staff = await staffContext.newPage();
    await adminSignIn(staff);
    await staff.goto(`/admin/support/${ticket.id}`);
    await expect(staff.getByTestId("message-customer")).toContainText("Is my size available?");

    const reply = uniqueText("Yes it is");
    await staff.getByTestId("reply-text").fill(reply);
    await staff.getByTestId("reply-send").click();
    await expect(staff.getByTestId("message-staff")).toContainText(reply);
    await expect(staff.getByTestId("ticket-status").first()).toHaveText("In progress");

    const note = uniqueText("Check the courier log");
    await staff.getByTestId("internal-switch").click();
    await expect(staff.getByText("Only staff can see this note. The customer is not told.")).toBeVisible();
    await staff.getByTestId("reply-text").fill(note);
    await staff.getByTestId("reply-send").click();
    const noteBox = staff.getByTestId("message-internal").filter({ hasText: note });
    await expect(noteBox).toBeVisible();
    await expect(noteBox).toContainText("Internal note, not visible to the customer");

    await page.goto(`/account/support/${ticket.id}`);
    await expect(page.getByTestId("message-staff")).toContainText(reply);
    await expect(page.getByTestId("message-staff")).toContainText("Juvo BD Support");
    await expect(page.getByText(note)).toHaveCount(0);
    await expect(page.getByTestId("message-internal")).toHaveCount(0);
    await staffContext.close();
  });

  test("the status flow: waiting, resolved, closed and reopened with the history", async ({ page }) => {
    const ticket = await staffTicket({ mobile: freshMobile() });
    await adminSignIn(page);
    await page.goto(`/admin/support/${ticket.id}`);
    await expect(status(page)).toHaveText("Open");

    await page.getByTestId("action-inProgress").click();
    await expect(status(page)).toHaveText("In progress");
    await page.getByTestId("action-waiting").click();
    await expect(status(page)).toHaveText("Waiting for customer");

    await page.getByTestId("action-resolve").click();
    await expect(page.getByRole("dialog").getByText("Mark this ticket as resolved?")).toBeVisible();
    await page.getByRole("dialog").getByRole("button", { name: "Mark resolved" }).click();
    await expect(status(page)).toHaveText("Resolved");

    await page.getByTestId("action-close").click();
    await page.getByRole("dialog").getByLabel("Reason (optional)").fill("Customer confirmed");
    await page.getByRole("dialog").getByRole("button", { name: "Close ticket" }).click();
    await expect(status(page)).toHaveText("Closed");

    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(page.getByTestId("action-reopen")).toBeVisible();
    await page.getByTestId("action-reopen").click();
    await expect(page.getByRole("dialog").getByText("Reopen this ticket?")).toBeVisible();
    await page.getByRole("dialog").getByRole("button", { name: "Reopen" }).click();
    await expect(status(page)).toHaveText("Open");
    await expect(page.getByTestId("ticket-facts")).toContainText("Reopened");

    const history = page.getByTestId("ticket-history");
    await expect(history).toContainText("Open to In progress");
    await expect(history).toContainText("Resolved to Closed");
    await expect(history).toContainText("Customer confirmed");
  });

  test("a closed ticket takes only internal notes, and an expired reopen window is explained", async ({ page }) => {
    const ticket = await staffTicket({ mobile: freshMobile() });
    await staffStep(ticket.id, "close", { reason: "done" });
    await adminSignIn(page);
    await page.goto(`/admin/support/${ticket.id}`);
    await expect(page.getByText("This ticket is closed. You can add an internal note")).toBeVisible();
    await expect(page.getByTestId("internal-switch")).toBeDisabled();
    await expect(page.getByTestId("canned-toggle")).toHaveCount(0);

    // Move the closing back 30 days: the window (7 days) has passed.
    sql(`SET QUOTED_IDENTIFIER ON; UPDATE Tickets SET ClosedAt = DATEADD(day, -30, SYSDATETIMEOFFSET()) WHERE Id = '${ticket.id}'`);
    await page.reload();
    await expect(page.getByText("This ticket was resolved or closed too long ago to reopen.")).toBeVisible();
    await expect(page.getByTestId("action-reopen")).toHaveCount(0);
  });

  test("saved replies are inserted for the ticket in English and in Bengali", async ({ page }) => {
    const mobile = freshMobile();
    const ticket = await staffTicket({ mobile, name: "Nadia Rahman" });
    const en = uniqueText("Late parcel EN");
    const bn = uniqueText("Late parcel BN");
    await canned(en, "Hello {customerName}, about {ticketNumber}: we are checking.", "en");
    await canned(bn, "প্রিয় {customerName}, {ticketNumber} নিয়ে আমরা দেখছি।", "bn");
    await adminSignIn(page);
    await page.goto(`/admin/support/${ticket.id}`);

    await page.getByTestId("canned-toggle").click();
    await expect(page.getByTestId("canned-picker")).toBeVisible();
    await pick(page, page.locator("#cp-reply"), en);
    await expect(page.getByTestId("reply-text")).toHaveValue(`Hello Nadia Rahman, about ${ticket.number}: we are checking.`);

    await pick(page, page.locator("#cp-language"), "Bengali");
    await pick(page, page.locator("#cp-reply"), bn);
    await expect(page.getByTestId("reply-text")).toHaveValue(new RegExp(`প্রিয় Nadia Rahman, ${ticket.number} নিয়ে আমরা দেখছি।`));
    await page.getByTestId("reply-send").click();
    await expect(page.getByTestId("message-staff").last()).toContainText("প্রিয় Nadia Rahman");
  });

  test("assign and priority changes are saved", async ({ page }) => {
    const ticket = await staffTicket({ mobile: freshMobile() });
    await adminSignIn(page);
    await page.goto(`/admin/support/${ticket.id}`);
    await pick(page, page.locator("#h-priority"), "Urgent");
    await expect(page.getByText("Priority saved.").first()).toBeVisible();
    await expect(page.getByTestId("ticket-priority").first()).toHaveText("Urgent");
    await pick(page, page.locator("#h-assignee"), loadState().admin.name);
    await expect(page.getByText("Assignment saved.").first()).toBeVisible();
    await expect(page.getByTestId("message-internal").first()).toBeVisible();
  });
});

test.describe("Support: staff list", () => {
  test("the chips equal the API summary and filter the list", async ({ page }) => {
    await staffTicket({ mobile: freshMobile() });
    const urgent = await staffTicket({ mobile: freshMobile(), priority: "Urgent" });
    await adminSignIn(page);
    await page.goto("/admin/support");
    const s = await summary();
    for (const key of ["Open", "InProgress", "Waiting", "Resolved", "Closed"]) {
      await expect(page.getByTestId(`chip-${key}`)).toContainText(String(s.byStatus[key] ?? 0));
    }
    await expect(page.getByTestId("chip-unassigned")).toContainText(String(s.unassigned));
    await page.getByTestId("chip-Resolved").click();
    await expect(page.getByTestId("chip-Resolved")).toHaveAttribute("aria-pressed", "true");

    await page.getByTestId("chip-Resolved").click();
    await page.getByLabel("Search tickets").fill(urgent.subject);
    await expect(page.locator("[data-testid=ticket-link]:visible")).toHaveCount(1);
    await expect(page.getByTestId("ticket-priority").first()).toHaveText("Urgent");
  });

  test("bulk assign and bulk close list every outcome", async ({ page }) => {
    const tag = uniqueText("Bulk");
    const a = await staffTicket({ mobile: freshMobile(), subject: `${tag} one` });
    const b = await staffTicket({ mobile: freshMobile(), subject: `${tag} two` });
    const c = await staffTicket({ mobile: freshMobile(), subject: `${tag} three` });
    await staffStep(c.id, "close", { reason: "x" });
    await adminSignIn(page);
    await page.goto("/admin/support");
    await page.getByLabel("Search tickets").fill(tag);
    await expect(page.locator("[data-testid=ticket-link]:visible")).toHaveCount(3);

    await page.getByRole("checkbox", { name: `Select ticket ${a.number}` }).click();
    await page.getByRole("checkbox", { name: `Select ticket ${b.number}` }).click();
    await expect(page.getByTestId("bulk-bar")).toContainText("2 tickets chosen");
    await page.getByTestId("bulk-bar").getByRole("button", { name: "Assign" }).click();
    await pick(page, page.getByRole("dialog").locator("#bulk-person"), "Me (");
    await page.getByRole("dialog").getByRole("button", { name: "Assign" }).click();
    await expect(page.getByRole("dialog").getByTestId("bulk-result")).toHaveCount(2);
    await expect(page.getByRole("dialog")).toContainText("2 done, 0 not done.");
    await page.getByRole("dialog").getByRole("button", { name: "Close", exact: true }).first().click();

    // Bulk close: the already closed ticket is refused on its own, the others close.
    await page.getByRole("checkbox", { name: `Select ticket ${a.number}` }).click();
    await page.getByRole("checkbox", { name: `Select ticket ${c.number}` }).click();
    await page.getByTestId("bulk-bar").getByRole("button", { name: "Close tickets" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Close tickets" }).click();
    await expect(page.getByRole("dialog").getByTestId("bulk-result")).toHaveCount(2);
    await expect(page.getByRole("dialog")).toContainText("1 done, 1 not done.");
    await expect(page.getByRole("dialog")).toContainText("That step is not possible for this ticket now.");
  });

  test("a staff member creates a ticket for a customer and lands on it", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/support");
    await page.getByTestId("new-ticket").click();
    await page.getByTestId("create-ticket-submit").click();
    await expect(page.getByRole("dialog").getByText("This is required.").first()).toBeVisible();
    const subject = uniqueText("Facebook message");
    await page.getByLabel("Customer mobile").fill(freshMobile());
    await page.getByLabel("Customer name").fill("Rahim Uddin");
    await pick(page, page.locator("#ct-channel"), "Facebook");
    await pick(page, page.locator("#ct-priority"), "High");
    await page.getByLabel("Subject").fill(subject);
    await page.getByLabel("What the customer said").fill("Asked about the exchange.");
    await page.locator("#ct-file").setInputFiles({ name: "chat.png", mimeType: "image/png", buffer: makePng(1200, 900) });
    await expect(page.getByTestId("ct-list")).toContainText("chat.png");
    await page.getByTestId("create-ticket-submit").click();
    await expect(page).toHaveURL(/\/admin\/support\/[0-9a-f-]{36}$/);
    await expect(page.getByTestId("ticket-priority").first()).toHaveText("High");
    await expect(page.getByTestId("message-customer")).toContainText("Asked about the exchange.");
    await expect(page.getByRole("link", { name: "Open chat.png" })).toBeVisible();
  });
});

test.describe("Support: saved replies manager and report", () => {
  test("create with preview, refuse a duplicate title, edit and delete", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/support");
    await page.getByRole("tab", { name: "Canned replies" }).click();
    await page.getByTestId("new-canned").click();
    const title = uniqueText("Refund info");
    await page.getByLabel("Title").fill(title);
    await page.getByLabel("Text").fill("Hello {customerName}, ticket {ticketNumber} and {wrong}.");
    await expect(page.getByText("These placeholders are not allowed: {wrong}.")).toBeVisible();
    await expect(page.getByTestId("canned-preview")).toContainText("Hello Nadia Rahman, ticket TK-2611-000007 and {wrong}.");
    await page.getByTestId("canned-save").click();
    await expect(page.getByText("Remove the placeholders that are not allowed: {wrong}.")).toBeVisible();
    await page.getByLabel("Text").fill("Hello {customerName}, ticket {ticketNumber}.");
    await page.getByTestId("canned-save").click();
    await expect(page.getByText("Canned reply created.").first()).toBeVisible();
    await expect(page.getByTestId("canned-item").filter({ hasText: title })).toBeVisible();

    await page.getByTestId("new-canned").click();
    await page.getByLabel("Title").fill(title);
    await page.getByLabel("Text").fill("Same title again");
    await page.getByTestId("canned-save").click();
    await expect(page.getByText("A canned reply with this title already exists in this language.")).toBeVisible();
    await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();

    await page.getByRole("button", { name: `Edit ${title}` }).click();
    await page.getByLabel("Text").fill("Updated text for {orderNumber}");
    await page.getByTestId("canned-save").click();
    await expect(page.getByTestId("canned-item").filter({ hasText: title })).toContainText("Updated text for {orderNumber}");

    await page.getByRole("button", { name: `Delete ${title}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete" }).click();
    await expect(page.getByTestId("canned-item").filter({ hasText: title })).toHaveCount(0);
  });

  test("the report shows exactly what was created and resolved", async ({ page }) => {
    const from = bdDay(1);
    const to = bdDay(0);
    const before = await reportOf(from, to);
    const a = await staffTicket({ mobile: freshMobile(), category: "delivery", priority: "Urgent" });
    await staffTicket({ mobile: freshMobile(), category: "delivery" });
    await staffTicket({ mobile: freshMobile(), category: "payment" });
    await staffStep(a.id, "reply", { text: "Looking into it", internal: false, setStatus: null, attachmentPaths: [] });
    await staffStep(a.id, "resolve", { reason: "done" });

    await adminSignIn(page);
    await page.goto("/admin/support");
    await page.getByRole("tab", { name: "Report" }).click();
    await page.getByLabel("From").fill(from);
    await page.getByLabel("Until").fill(to);
    const after = await reportOf(from, to);
    expect(after.newTickets).toBe(before.newTickets + 3);
    expect(after.resolvedTickets).toBe(before.resolvedTickets + 1);
    await expect(page.getByTestId("kpi-new")).toContainText(String(after.newTickets));
    await expect(page.getByTestId("kpi-resolved")).toContainText(String(after.resolvedTickets));
    await expect(page.getByTestId("kpi-open")).toContainText(String(after.openNow));
    await expect(page.getByTestId("daily-summary")).toContainText(`${after.newTickets} new and ${after.resolvedTickets} resolved`);
    await expect(page.getByTestId("first-response")).not.toContainText("No data");
    await expect(page.getByTestId("bar-delivery")).toBeVisible();
    await expect(page.getByTestId("by-assignee")).toBeVisible();
    await expect(page.getByTestId("oldest-open")).toBeVisible();
  });
});

test.describe("Support: who may do what", () => {
  test("a role that can only view tickets cannot reply or change anything", async ({ page }) => {
    const ticket = await staffTicket({ mobile: freshMobile() });
    const viewer = await staffWithPermissions(["support.ticket.view"]);
    await signInViaBff(page.context(), viewer.email, viewer.password);
    await page.goto(`/admin/support/${ticket.id}`);
    await expect(page.getByRole("heading", { level: 1, name: ticket.number })).toBeVisible();
    await expect(page.getByText("You can read this ticket but you are not allowed to reply to it.")).toBeVisible();
    await expect(page.getByTestId("reply-send")).toHaveCount(0);
    await expect(page.getByTestId("action-resolve")).toHaveCount(0);
    await page.goto("/admin/support");
    await expect(page.getByTestId("new-ticket")).toHaveCount(0);
    await expect(page.getByRole("checkbox", { name: /Select ticket/ })).toHaveCount(0);
  });

  test("a role without the view permission is sent to the forbidden page", async ({ page }) => {
    const reporter = await staffWithPermissions(["support.report.view"]);
    await signInViaBff(page.context(), reporter.email, reporter.password);
    await page.goto("/admin/support");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("a role with reply but not close cannot close, and the buttons say so", async ({ page }) => {
    const ticket = await staffTicket({ mobile: freshMobile() });
    const replier = await staffWithPermissions(["support.ticket.view", "support.ticket.reply"]);
    await signInViaBff(page.context(), replier.email, replier.password);
    await page.goto(`/admin/support/${ticket.id}`);
    await expect(page.getByTestId("action-resolve")).toBeVisible();
    await expect(page.getByTestId("action-close")).toHaveCount(0);
    await expect(page.getByText("Some steps are hidden because your role does not allow them.")).toBeVisible();
    await page.getByTestId("reply-text").fill("Thanks for waiting");
    await page.getByTestId("reply-send").click();
    await expect(page.getByTestId("message-staff")).toContainText("Thanks for waiting");
  });
});
