import { test, expect, type Browser, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { call } from "./support/cod";
import { loadState } from "./support/env";
import { adminAccess, freshMobile, staffWithPermissions } from "./support/returns";
import { signInViaBff } from "./support/ui";
import { resetBrand } from "./support/content";
import { findMessages, freeAddress, sendMessage, uniqueToken } from "./support/contact";

/**
 * The contact page and the staff inbox in the browser against the real API: a visitor writes (checks, Bengali digits, honeypot, hourly
 * limit, reference), the brand block shows only what is filled in, and staff find the message, filter, set a status, add a note,
 * archive and restore; masked contact data and read-only rights are respected. The API counts messages per client address, so each
 * test frees the address budget in the test database (support/contact.ts); production limits are untouched.
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

async function visitor(browser: Browser, locale: "en" | "bn" = "en") {
  const context = await browser.newContext({ baseURL: E2E.webUrl });
  await context.addCookies([{ name: "juvo_locale", value: locale, url: E2E.webUrl }]);
  return { context, page: await context.newPage() };
}

test.afterEach(() => freeAddress());
test.afterAll(async () => {
  await resetBrand();
});

async function fillForm(page: Page, v: { name?: string; mobile?: string; email?: string; subject?: string; message?: string }) {
  if (v.name !== undefined) await page.getByLabel("Your name").fill(v.name);
  if (v.mobile !== undefined) await page.getByLabel("Mobile number").fill(v.mobile);
  if (v.email !== undefined) await page.getByLabel("E-mail", { exact: true }).fill(v.email);
  if (v.subject !== undefined) await page.getByLabel("Subject (optional)").fill(v.subject);
  if (v.message !== undefined) await page.getByLabel("Your message").fill(v.message);
}

test.describe("Contact page (visitor)", () => {
  test("checks the form, then sends and shows the reference; staff see the message as New", async ({ browser }) => {
    const { context, page } = await visitor(browser);
    await page.goto("/contact");
    await expect(page.getByRole("heading", { level: 1, name: "Contact us" })).toBeVisible();

    // Nothing filled: name, contact and message are asked for in plain words.
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByText("This is required.").first()).toBeVisible();
    await expect(page.getByText("Give a mobile number or an e-mail so we can answer.").first()).toBeVisible();
    await expect(page.getByTestId("contact-sent")).toHaveCount(0);

    // Bad values.
    await fillForm(page, { name: "Nadia", mobile: "12345", email: "nope", message: "hi" });
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByText("Enter a Bangladesh mobile number such as 01712345678.")).toBeVisible();
    await expect(page.getByText("Enter an e-mail address such as name@example.com.")).toBeVisible();
    await expect(page.getByText("Please write a little more (at least 5 characters).")).toBeVisible();

    // Markup is refused with a friendly message.
    await fillForm(page, { mobile: "", email: "nadia@example.com", message: "Hello <script>alert(1)</script> there" });
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByText("Please use plain text only. Remove HTML tags and code.")).toBeVisible();

    // A good message with Bengali digits in the mobile number.
    const token = uniqueToken("Late parcel");
    const mobile = freshMobile();
    const bengali = mobile.replace(/\d/g, (d) => "০১২৩৪৫৬৭৮৯"[Number(d)]!);
    await fillForm(page, { name: "Nadia Rahman", mobile: bengali, email: "", subject: token, message: "Where is my parcel? It was due yesterday." });
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByTestId("contact-sent")).toBeVisible();
    const reference = (await page.getByTestId("contact-reference").textContent())!.trim();
    expect(reference).toMatch(/^CM-[0-9A-F]{8}$/);

    const found = await findMessages(token);
    expect(found).toHaveLength(1);
    expect(found[0]!.reference).toBe(reference);
    expect(found[0]!.status).toBe("New");
    expect(found[0]!.mobile).toBe("+880" + mobile.slice(1));
    await context.close();
  });

  test("the honeypot field is out of reach for people and a filled one stores nothing", async ({ browser }) => {
    const { context, page } = await visitor(browser);
    await page.goto("/contact");
    const trap = page.getByTestId("contact-honeypot");
    await expect(trap).toHaveAttribute("name", "website");
    await expect(trap).toHaveAttribute("tabindex", "-1");
    await expect(page.getByTestId("contact-honeypot-wrap")).toHaveAttribute("aria-hidden", "true");
    expect(await page.getByTestId("contact-honeypot-wrap").evaluate((el) => (el as HTMLElement).inert)).toBe(true);
    // Keyboard: tabbing through the whole form never lands on it.
    await page.getByLabel("Your name").focus();
    for (let i = 0; i < 6; i++) {
      await page.keyboard.press("Tab");
      expect(await page.evaluate(() => document.activeElement?.id)).not.toBe("cm-website");
    }

    // A bot fills it: the page still says thanks (so the bot learns nothing) but nothing is stored.
    const token = uniqueToken("Bot");
    await fillForm(page, { name: "Robo", mobile: freshMobile(), subject: token, message: "Buy cheap watches now" });
    await trap.evaluate((el) => {
      (el as HTMLInputElement).value = "http://spam.example";
    });
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByTestId("contact-sent")).toBeVisible();
    expect(await findMessages(token)).toHaveLength(0);
    await context.close();
  });

  test("the hourly limit per mobile number is explained in plain words", async ({ browser }) => {
    const mobile = freshMobile();
    for (let i = 0; i < 3; i++) await sendMessage({ mobile });
    const { context, page } = await visitor(browser);
    await page.goto("/contact");
    await fillForm(page, { name: "Nadia", mobile, message: "One more message please" });
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByTestId("contact-failure")).toContainText("You have sent several messages in the last hour.");
    await expect(page.getByTestId("contact-sent")).toHaveCount(0);
    await context.close();
  });

  test("the brand block shows only what is filled in, and the footer leads to the order-help form", async ({ browser }) => {
    const token = await adminAccess();
    await call("PUT", "/api/v1/content/brand", {
      token,
      expect: 200,
      body: { businessName: "Juvo BD", email: "hello@juvo.test", phone: "02-5551234", whatsApp: "01712345678", addressEn: "House 12, Road 5, Dhanmondi, Dhaka", workingHoursEn: "Sat to Thu, 10 am to 8 pm", latitude: 23.7461, longitude: 90.3742 },
    });
    const { context, page } = await visitor(browser);
    await page.goto("/contact");
    const brand = page.getByTestId("contact-brand");
    await expect(brand).toContainText("House 12, Road 5, Dhanmondi, Dhaka");
    await expect(brand.getByRole("link", { name: /02-5551234/ })).toHaveAttribute("href", "tel:025551234");
    await expect(brand.getByRole("link", { name: /Chat on WhatsApp/ })).toHaveAttribute("href", "https://wa.me/8801712345678");
    await expect(brand.getByRole("link", { name: /hello@juvo.test/ })).toHaveAttribute("href", "mailto:hello@juvo.test");
    await expect(brand).toContainText("Sat to Thu, 10 am to 8 pm");
    await expect(page.getByTestId("contact-map-link")).toHaveAttribute("href", /openstreetmap\.org\/\?mlat=23\.7461&mlon=90\.3742/);

    await resetBrand();
    await page.reload();
    await expect(page.getByTestId("contact-brand")).toContainText("juvobd@gmail.com"); // only the e-mail of the reset brand is left
    await expect(page.getByTestId("contact-brand")).not.toContainText("Dhanmondi");
    await expect(page.getByTestId("contact-brand").getByRole("link", { name: /Chat on WhatsApp/ })).toHaveCount(0);
    await expect(page.getByTestId("contact-map-link")).toHaveCount(0);

    // The old ticket form lives on at /contact/order-help, linked from the page and from the footer.
    await page.getByTestId("order-help-page-link").click();
    await expect(page).toHaveURL(/\/contact\/order-help$/);
    await expect(page.getByTestId("contact-form")).toBeVisible();
    await page.goto("/contact");
    await expect(page.getByRole("contentinfo").getByRole("link", { name: "Help with an order" })).toHaveAttribute("href", "/contact/order-help");
    await expect(page.getByRole("contentinfo").getByRole("link", { name: "Contact us and help" })).toHaveAttribute("href", "/contact");
    await context.close();
  });

  test("the page and the confirmation are in Bengali for a Bengali visitor", async ({ browser }) => {
    const { context, page } = await visitor(browser, "bn");
    await page.goto("/contact");
    await expect(page.getByRole("heading", { level: 1, name: "যোগাযোগ করুন" })).toBeVisible();
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByText("এটি আবশ্যক।").first()).toBeVisible();
    await page.getByLabel("আপনার নাম").fill("নাদিয়া");
    await page.getByLabel("ই-মেইল", { exact: true }).fill("nadia@example.com");
    await page.getByLabel("আপনার বার্তা").fill("আমার পার্সেল কোথায়?");
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByTestId("contact-sent")).toContainText("ধন্যবাদ");
    await context.close();
  });
});

test.describe("Contact inbox (staff)", () => {
  test("find the message, filter, set statuses, add a note, archive and restore", async ({ page }) => {
    test.setTimeout(120_000);
    const first = await sendMessage({ subject: uniqueToken("Inbox flow") });
    const token = first.token;
    // Two more messages sharing a search word, so the status filter has something to separate.
    const word = uniqueToken("Shared");
    const a = await sendMessage({ subject: `${word} A` });
    const b = await sendMessage({ subject: `${word} B`, email: "buyer@example.com", mobile: null });

    await adminSignIn(page);
    await page.goto("/admin/content/pages");
    await expect(page.getByRole("navigation").getByRole("link", { name: "Contact messages" }).first()).toBeAttached();
    await page.goto("/admin/content/messages");
    await expect(page.getByRole("heading", { level: 1, name: "Contact messages" })).toBeVisible();
    await expect(page.getByTestId("filter-New")).toContainText(/New \(\d+\)/);

    const rows = page.locator("[data-testid=message-link]:visible");
    await page.getByLabel("Search messages").fill(word);
    await expect(rows).toHaveCount(2);
    await page.getByTestId("filter-New").click();
    await expect(rows).toHaveCount(2);
    await page.getByTestId("filter-Read").click();
    await expect(rows).toHaveCount(0);
    await page.getByTestId("filter-all").click();

    // Opening a message does not change its status.
    await page.getByRole("link", { name: a.reference }).first().click();
    await expect(page.getByTestId("message-card")).toContainText(`${word} A`);
    await expect(page.getByTestId("message-status")).toHaveAttribute("data-status", "New");
    await expect(page.getByTestId("message-mobile")).toHaveText("+880" + a.mobile.slice(1));
    await expect(page.getByTestId("pii-masked")).toHaveCount(0);
    await expect(page.getByTestId("message-handled")).toContainText("Nobody has handled this message yet.");
    await page.reload();
    await expect(page.getByTestId("message-status")).toHaveAttribute("data-status", "New");

    // Mark as read, then replied.
    await page.getByTestId("set-Read").click();
    await expect(page.getByTestId("message-status")).toHaveAttribute("data-status", "Read");
    await expect(page.getByTestId("message-handled")).toContainText("Last handled");
    await page.getByTestId("set-Replied").click();
    await expect(page.getByTestId("message-status")).toHaveAttribute("data-status", "Replied");

    // A note is appended with the date and the author, and a second note follows the first.
    await page.getByLabel("New note").fill("Called her, parcel is on the way");
    await page.getByTestId("note-add").click();
    await expect(page.getByTestId("message-notes")).toContainText("Called her, parcel is on the way");
    await page.getByLabel("New note").fill("Second call, delivered");
    await page.getByTestId("note-add").click();
    await expect(page.getByTestId("message-notes")).toContainText("Second call, delivered");
    await expect(page.getByTestId("message-notes")).toContainText("Called her, parcel is on the way");
    await expect(page.getByTestId("message-notes")).toContainText(/\[.*UTC.*\]/);
    // An empty note is refused before sending.
    await page.getByTestId("note-add").click();
    await expect(page.getByText("Write something first.")).toBeVisible();

    // Archive, find it under Archived, restore it.
    await page.getByTestId("archive").click();
    await expect(page.getByTestId("message-status")).toHaveAttribute("data-status", "Archived");
    await expect(page.getByTestId("archive")).toHaveCount(0);
    await page.goto("/admin/content/messages");
    await page.getByLabel("Search messages").fill(word);
    await page.getByTestId("filter-Archived").click();
    await expect(rows).toHaveCount(1);
    await page.getByTestId("filter-all").click();
    await page.getByTestId("filter-New").click();
    await expect(rows).toHaveCount(1); // the other one is still New
    await page.getByTestId("filter-Archived").click();
    await page.getByRole("link", { name: a.reference }).first().click();
    await page.getByTestId("set-New").click();
    await expect(page.getByTestId("message-status")).toHaveAttribute("data-status", "New");
    await expect(page.getByTestId("message-handled")).toContainText("Nobody has handled this message yet."); // New clears the handled stamp

    // The e-mail-only message shows no mobile line.
    await page.goto("/admin/content/messages");
    await page.getByLabel("Search messages").fill(`${word} B`);
    await page.getByRole("link", { name: b.reference }).first().click();
    await expect(page.getByTestId("message-email")).toHaveText("buyer@example.com");
    await expect(page.getByTestId("message-mobile")).toHaveCount(0);

    // The search by message text works too.
    await page.goto("/admin/content/messages");
    await page.getByLabel("Search messages").fill(token);
    await expect(rows).toHaveCount(1);
  });

  test("without the customer-details permission contact data stays masked", async ({ page }) => {
    const sent = await sendMessage({ email: "private.person@example.com" });
    const staff = await staffWithPermissions(["content.message.view", "content.message.handle"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin/content/messages");
    await page.getByLabel("Search messages").fill(sent.token);
    await expect(page.locator("[data-testid=message-link]:visible")).toHaveCount(1);
    await expect(page.getByTestId("pii-note")).toBeVisible();
    await expect(page.getByText("private.person@example.com")).toHaveCount(0);
    await page.getByRole("link", { name: sent.reference }).first().click();
    await expect(page.getByTestId("pii-masked")).toBeVisible();
    await expect(page.getByTestId("message-mobile")).not.toHaveText("+880" + sent.mobile.slice(1));
    await expect(page.getByTestId("message-email")).not.toHaveText("private.person@example.com");
    // Handling still works for this role.
    await page.getByTestId("set-Read").click();
    await expect(page.getByTestId("message-status")).toHaveAttribute("data-status", "Read");
  });

  test("view only: the message can be read but not changed", async ({ page }) => {
    const sent = await sendMessage();
    const staff = await staffWithPermissions(["content.message.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin/content/messages");
    await page.getByLabel("Search messages").fill(sent.token);
    await page.getByRole("link", { name: sent.reference }).first().click();
    await expect(page.getByTestId("message-read-only")).toBeVisible();
    await expect(page.getByTestId("message-text")).toContainText(sent.token);
    await expect(page.getByTestId("message-actions")).toHaveCount(0);
    await expect(page.getByTestId("note-add")).toHaveCount(0);
    await expect(page.getByTestId("set-Read")).toHaveCount(0);
    await expect(page.getByTestId("archive")).toHaveCount(0);
  });

  test("no view permission: no menu item and the screen is refused", async ({ page }) => {
    const staff = await staffWithPermissions(["content.page.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin/content/pages");
    await expect(page.getByRole("navigation").getByRole("link", { name: "Contact messages" })).toHaveCount(0);
    await page.goto("/admin/content/messages");
    await expect(page).toHaveURL(/\/forbidden/);
  });

  test("an unknown message shows a plain not-found state", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/content/messages/00000000-0000-4000-8000-000000000000");
    await expect(page.getByText("This message was not found.")).toBeVisible();
  });
});
