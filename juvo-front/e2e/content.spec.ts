import { test, expect, type Browser, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { signInViaBff } from "./support/ui";
import { publishPage, readBrand, readPage, readSocial, resetContent } from "./support/content";

/**
 * Site-content screens in the browser against the real API: a page is written, previewed, saved, published, read on the shop, then
 * unpublished; unsafe markup is refused with the API's message; brand details reach the footer; social links accept https only; trust
 * badges show on the home page; and a staff member with view rights only gets read-only screens. Every test starts from the seeded
 * content (reset through the staff API) and leaves it that way.
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

/** A visitor with no session and the shop's language cookie. */
async function visitor(browser: Browser, locale: "en" | "bn" = "en") {
  const context = await browser.newContext({ baseURL: E2E.webUrl });
  await context.addCookies([{ name: "juvo_locale", value: locale, url: E2E.webUrl }]);
  return { context, page: await context.newPage() };
}

const toast = (page: Page, text: string) => expect(page.getByText(text).first()).toBeVisible();

test.beforeEach(async () => {
  await resetContent();
});
test.afterAll(async () => {
  await resetContent();
});

test.describe("Content pages", () => {
  test("write, preview, save, publish, read on the shop, then unpublish", async ({ page, browser }) => {
    await adminSignIn(page);
    await page.goto("/admin/content/pages");
    await expect(page.getByRole("heading", { level: 1, name: "Website pages" })).toBeVisible();
    await expect(page.getByTestId("pages-list").getByTestId("page-status")).toHaveCount(6);
    await expect(page.getByTestId("page-row-about").getByTestId("page-status")).toHaveAttribute("data-status", "draft");

    await page.getByTestId("page-edit-about").click();
    await expect(page.getByTestId("page-editor")).toBeVisible();
    // Nothing saved yet: publishing is not possible and the screen says why.
    await expect(page.getByTestId("page-publish")).toBeDisabled();
    await expect(page.getByText("To publish, save an English title and English text first.")).toBeVisible();

    await page.locator("#pg-title-En").fill("About Juvo");
    await page.locator("#pg-body-En").fill("# Our story\n\nWe sell **fashion** and *shoes*.\n\n- Clothing\n- Purses\n\nVisit [our shop](https://example.com/shop).");
    await page.locator("#pg-mt-En").fill("About Juvo BD");
    await page.locator("#pg-md-En").fill("Who we are.");
    const preview = page.getByTestId("page-preview-body");
    await expect(preview.getByRole("heading", { name: "Our story" })).toBeVisible();
    await expect(preview.locator("strong")).toHaveText("fashion");
    await expect(preview.getByRole("listitem")).toHaveCount(2);
    await expect(preview.getByRole("link", { name: "our shop" })).toHaveAttribute("href", "https://example.com/shop");
    await expect(page.getByTestId("page-publish")).toBeDisabled(); // unsaved changes block publishing

    await page.getByTestId("page-save").click();
    await toast(page, "Page saved.");
    expect((await readPage("about")).titleEn).toBe("About Juvo");
    await expect(page.getByTestId("page-status")).toHaveAttribute("data-status", "draft");

    // Still a draft: the shop says "not available yet" with a real 404.
    const anon = await visitor(browser);
    const draft = await anon.page.goto("/about");
    expect(draft?.status()).toBe(404);
    await expect(anon.page.getByTestId("content-not-available")).toContainText("This page is not available yet");

    await page.getByTestId("page-publish").click();
    await expect(page.getByRole("dialog")).toContainText("Customers will be able to open this page at /about");
    await page.getByRole("dialog").getByRole("button", { name: "Publish" }).click();
    await toast(page, "Page published. Customers can read it now.");
    await expect(page.getByTestId("page-status")).toHaveAttribute("data-status", "published");
    expect((await readPage("about")).isPublished).toBe(true);

    const live = await anon.page.goto("/about");
    expect(live?.status()).toBe(200);
    await expect(anon.page.getByRole("heading", { level: 1, name: "About Juvo" })).toBeVisible();
    await expect(anon.page.getByTestId("content-page").getByRole("heading", { level: 2, name: "Our story" })).toBeVisible();
    await expect(anon.page.getByTestId("content-page").getByRole("listitem")).toHaveCount(2);
    await expect(anon.page.getByTestId("content-page").getByRole("link", { name: "our shop" })).toHaveAttribute("href", "https://example.com/shop");
    await expect(anon.page).toHaveTitle(/About Juvo BD/);
    await expect(anon.page.locator('meta[name="description"]')).toHaveAttribute("content", "Who we are.");
    await expect(anon.page.getByTestId("footer-pages").getByRole("link", { name: "About us" })).toHaveAttribute("href", "/about");

    await page.getByTestId("page-unpublish").click();
    await expect(page.getByRole("dialog")).toContainText("stops working at once");
    await page.getByRole("dialog").getByRole("button", { name: "Unpublish" }).click();
    await toast(page, "Page unpublished. Customers can no longer open it.");
    await expect(page.getByTestId("page-status")).toHaveAttribute("data-status", "draft");

    const gone = await anon.page.goto("/about");
    expect(gone?.status()).toBe(404);
    await expect(anon.page.getByTestId("content-not-available")).toBeVisible();
    await expect(anon.page.getByTestId("footer-pages")).toHaveCount(0);
    await anon.context.close();
    // The text stays saved after unpublishing.
    expect((await readPage("about")).titleEn).toBe("About Juvo");
  });

  test("Bengali readers see the Bengali text when it exists and the English text otherwise", async ({ browser }) => {
    await publishPage("faq", "Questions", "Ask us anything.");
    const bn = await visitor(browser, "bn");
    await bn.page.goto("/faq");
    await expect(bn.page.getByRole("heading", { level: 1, name: "Questions" })).toBeVisible(); // no Bengali yet: English
    await publishPage("faq", "Questions", "Ask us anything.", { titleBn: "প্রশ্নোত্তর", bodyBn: "যা খুশি জিজ্ঞেস করুন।" });
    await bn.page.goto("/faq");
    await expect(bn.page.getByRole("heading", { level: 1, name: "প্রশ্নোত্তর" })).toBeVisible();
    await expect(bn.page.getByText("যা খুশি জিজ্ঞেস করুন।")).toBeVisible();
    await bn.context.close();
  });

  test("all six public addresses answer: published pages show, the others are 404", async ({ browser }) => {
    await publishPage("privacy-policy", "Privacy", "We keep little.");
    await publishPage("terms", "Terms", "Be kind.");
    const anon = await visitor(browser);
    for (const [path, status] of [["/about", 404], ["/faq", 404], ["/delivery-policy", 404], ["/return-policy", 404], ["/privacy", 200], ["/terms", 200]] as const) {
      const response = await anon.page.goto(path);
      expect(response?.status(), path).toBe(status);
    }
    await expect(anon.page.getByTestId("footer-pages").getByRole("link")).toHaveCount(2); // only the two published pages are linked
    await anon.context.close();
  });

  test("raw HTML and script links are refused with the API's message, nothing is saved", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/content/pages/terms");
    await page.locator("#pg-title-En").fill("Terms");
    await page.locator("#pg-body-En").fill("Fine text\n\n<script>alert(1)</script>");
    // The preview shows it as plain text and never as markup.
    await expect(page.getByTestId("page-preview-body").locator("script")).toHaveCount(0);
    await expect(page.getByTestId("page-preview-body")).toContainText("<script>alert(1)</script>");
    await page.getByTestId("page-save").click();
    const error = page.getByTestId("page-error");
    await expect(error).toBeVisible();
    await expect(error).toContainText("Nothing was saved");
    expect((await readPage("terms")).bodyEn).toBeNull();

    await page.locator("#pg-body-En").fill("Click [here](javascript:alert(1))");
    await expect(page.getByTestId("page-preview-body").getByRole("link")).toHaveCount(0);
    await page.getByTestId("page-save").click();
    await expect(page.getByTestId("page-error")).toContainText("Nothing was saved");
    expect((await readPage("terms")).bodyEn).toBeNull();

    await page.locator("#pg-body-En").fill("Plain and safe.");
    await page.getByTestId("page-save").click();
    await toast(page, "Page saved.");
    await expect(page.getByTestId("page-error")).toHaveCount(0);
  });

  test("length limits are shown before saving", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/content/pages/faq");
    await page.locator("#pg-title-En").fill("x".repeat(201));
    await expect(page.getByText("Too long. The most allowed is 200 characters.").first()).toBeVisible();
    await page.getByTestId("page-save").click();
    expect((await readPage("faq")).titleEn).toBeNull();
  });
});

test.describe("Brand details", () => {
  test("save details, see them in the shop footer; invalid phone and half a map point are refused", async ({ page, browser }) => {
    await adminSignIn(page);
    await page.goto("/admin/content/brand");
    await expect(page.getByRole("heading", { level: 1, name: "Brand details" })).toBeVisible();
    await expect(page.locator("#br-name")).toHaveValue("Juvo BD");
    await expect(page.getByTestId("brand-logo")).toContainText("Uploading a logo is a later step");

    await page.locator("#br-phone").fill("call me");
    await page.getByTestId("brand-save").click();
    await expect(page.getByText("Use 6 to 20 digits, spaces and + - ( ) only.")).toBeVisible();
    await page.locator("#br-phone").fill("02-5551234");
    await page.locator("#br-lat").fill("23.8103");
    await page.getByTestId("brand-save").click();
    await expect(page.getByText("Enter both latitude and longitude, or leave both empty.")).toBeVisible();
    await page.locator("#br-lng").fill("90.4125");

    await page.locator("#br-wa").fill("01712345678");
    await page.locator("#br-addr-en").fill("House 12, Road 5, Dhanmondi, Dhaka");
    await page.locator("#br-addr-bn").fill("বাড়ি ১২, রোড ৫, ধানমন্ডি, ঢাকা");
    await page.locator("#br-hours-en").fill("Sat to Thu, 10 am to 8 pm");
    await page.getByTestId("brand-save").click();
    await toast(page, "Brand details saved.");
    const saved = await readBrand();
    expect(saved).toMatchObject({ phone: "02-5551234", whatsApp: "+8801712345678", addressEn: "House 12, Road 5, Dhanmondi, Dhaka", latitude: 23.8103, longitude: 90.4125 });

    const anon = await visitor(browser);
    await anon.page.goto("/");
    const block = anon.page.getByTestId("footer-brand");
    await expect(block).toContainText("House 12, Road 5, Dhanmondi, Dhaka");
    await expect(block).toContainText("Sat to Thu, 10 am to 8 pm");
    await expect(block.getByRole("link", { name: /02-5551234/ })).toHaveAttribute("href", "tel:025551234");
    await expect(block.getByRole("link", { name: /\+8801712345678/ })).toHaveAttribute("href", "https://wa.me/8801712345678");
    await expect(block.getByRole("link", { name: /juvobd@gmail.com/ })).toHaveAttribute("href", "mailto:juvobd@gmail.com");
    await anon.context.close();

    // Clearing a field hides its line again.
    await page.reload();
    await page.locator("#br-phone").fill("");
    await page.getByTestId("brand-save").click();
    await toast(page, "Brand details saved.");
    expect((await readBrand()).phone).toBeNull();
  });
});

test.describe("Social links", () => {
  test("https only: refuses http and bare names, accepts https, a WhatsApp number, hides and removes", async ({ page, browser }) => {
    await adminSignIn(page);
    await page.goto("/admin/content/social");
    await expect(page.getByRole("heading", { level: 1, name: "Social links" })).toBeVisible();
    await expect(page.getByTestId("social-list").getByRole("listitem")).toHaveCount(5);
    await expect(page.getByTestId("social-row-facebook").getByTestId("social-state")).toContainText("Shown on the shop");

    const ig = page.getByTestId("social-row-instagram");
    await page.locator("#so-instagram-url").fill("http://instagram.com/juvobd");
    await page.getByTestId("so-instagram-save").click();
    await expect(ig.getByText("The address must start with https://, not http://.")).toBeVisible();
    await page.locator("#so-instagram-url").fill("instagram.com/juvobd");
    await page.getByTestId("so-instagram-save").click();
    await expect(ig.getByText("Enter a full address starting with https://")).toBeVisible();
    expect((await readSocial()).some((s) => s.platform === "Instagram")).toBe(false);

    await page.locator("#so-instagram-url").fill("https://instagram.com/juvobd");
    await page.getByTestId("so-instagram-save").click();
    await toast(page, "Instagram saved.");
    await expect(ig.getByTestId("social-state")).toContainText("Shown on the shop");

    await page.locator("#so-whatsapp-url").fill("01712345678");
    await page.getByTestId("so-whatsapp-save").click();
    await toast(page, "WhatsApp saved.");
    expect((await readSocial()).find((s) => s.platform === "WhatsApp")?.url).toBe("https://wa.me/8801712345678");

    const anon = await visitor(browser);
    await anon.page.goto("/");
    const social = anon.page.getByTestId("footer-social");
    await expect(social.getByRole("link", { name: "Instagram" })).toHaveAttribute("href", "https://instagram.com/juvobd");
    await expect(social.getByRole("link", { name: "WhatsApp" })).toHaveAttribute("href", "https://wa.me/8801712345678");
    await expect(social.getByRole("link", { name: "Facebook" })).toBeVisible();

    // Switch Instagram off: it stays saved but leaves the shop.
    await page.locator("#so-instagram-on").click();
    await page.getByTestId("so-instagram-save").click();
    await toast(page, "Instagram saved.");
    await expect(ig.getByTestId("social-state")).toContainText("Switched off");
    await anon.page.goto("/");
    await expect(anon.page.getByTestId("footer-social").getByRole("link", { name: "Instagram" })).toHaveCount(0);

    // Remove WhatsApp after a confirmation.
    await page.getByTestId("so-whatsapp-remove").click();
    await expect(page.getByRole("dialog")).toContainText("The link is deleted");
    await page.getByRole("dialog").getByRole("button", { name: "Remove" }).click();
    await toast(page, "WhatsApp removed.");
    await expect(page.getByTestId("social-row-whatsapp").getByText("Not set up")).toBeVisible();
    expect((await readSocial()).some((s) => s.platform === "WhatsApp")).toBe(false);
    await anon.context.close();
  });
});

test.describe("Trust badges", () => {
  test("text in two languages, on and off, shown on the home page only when on and written", async ({ page, browser }) => {
    await adminSignIn(page);
    await page.goto("/admin/content/badges");
    await expect(page.getByRole("heading", { level: 1, name: "Trust badges" })).toBeVisible();
    await expect(page.getByTestId("badges-list").getByRole("listitem")).toHaveCount(4);

    const anon = await visitor(browser);
    await anon.page.goto("/");
    await expect(anon.page.getByTestId("trust-badges")).toHaveCount(0); // nothing written, nothing shown

    // A badge cannot be switched on without English text.
    await page.locator("#bd-secure-payment-on").click();
    await page.getByTestId("bd-secure-payment-save").click();
    await expect(page.getByText("Write the English text before switching this badge on.")).toBeVisible();

    await page.locator("#bd-secure-payment-en").fill("Pay safely with cash on delivery");
    await page.locator("#bd-secure-payment-bn").fill("ক্যাশ অন ডেলিভারিতে নিরাপদে পরিশোধ করুন");
    await page.getByTestId("bd-secure-payment-save").click();
    await toast(page, "Secure payment saved.");
    await expect(page.getByTestId("badge-row-secure-payment").getByTestId("badge-state")).toContainText("Shown on the shop");

    await anon.page.goto("/");
    await expect(anon.page.getByTestId("trust-badge-secure-payment")).toContainText("Pay safely with cash on delivery");
    await expect(anon.page.getByTestId("trust-badges").getByRole("listitem")).toHaveCount(1);
    await anon.context.close();

    const bn = await visitor(browser, "bn");
    await bn.page.goto("/");
    await expect(bn.page.getByTestId("trust-badge-secure-payment")).toContainText("ক্যাশ অন ডেলিভারিতে নিরাপদে পরিশোধ করুন");
    await bn.context.close();

    await page.locator("#bd-secure-payment-on").click();
    await page.getByTestId("bd-secure-payment-save").click();
    await toast(page, "Secure payment saved.");
    const again = await visitor(browser);
    await again.page.goto("/");
    await expect(again.page.getByTestId("trust-badges")).toHaveCount(0);
    await again.context.close();
  });
});

test.describe("Permissions", () => {
  test("view only: pages and brand are read-only, social links and badges are not offered", async ({ page }) => {
    await publishPage("faq", "Questions", "Ask us.");
    const staff = await staffWithPermissions(["content.page.view", "content.brand.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);

    await page.goto("/admin/content/pages");
    await expect(page.getByTestId("pages-read-only")).toBeVisible();
    await page.getByTestId("page-edit-faq").click();
    await expect(page.getByTestId("page-read-only")).toBeVisible();
    await expect(page.locator("#pg-title-En")).toBeDisabled();
    await expect(page.getByTestId("page-save")).toHaveCount(0);
    await expect(page.getByTestId("page-publish")).toHaveCount(0);
    await expect(page.getByTestId("page-unpublish")).toHaveCount(0);

    await page.goto("/admin/content/brand");
    await expect(page.getByTestId("brand-read-only")).toBeVisible();
    await expect(page.locator("#br-name")).toBeDisabled();
    await expect(page.getByTestId("brand-save")).toHaveCount(0);

    await page.goto("/admin/content/social");
    await expect(page).toHaveURL(/\/forbidden/);
    await page.goto("/admin/content/badges");
    await expect(page).toHaveURL(/\/forbidden/);
  });

  test("edit without publish: can save a page but not publish it", async ({ page }) => {
    const staff = await staffWithPermissions(["content.page.view", "content.page.edit"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin/content/pages/about");
    await expect(page.getByTestId("page-read-only")).toHaveCount(0);
    await page.locator("#pg-title-En").fill("Draft title");
    await page.getByTestId("page-save").click();
    await toast(page, "Page saved.");
    await expect(page.getByTestId("page-publish")).toHaveCount(0);
  });
});
