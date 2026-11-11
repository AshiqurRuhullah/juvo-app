import { test, expect, type Page } from "@playwright/test";

/**
 * Customer sign-in with the one-time code: the customer chooses SMS or email, and in sandbox mode (the e2e API runs in Development)
 * the code is shown on the screen, so nobody waits for a message that no provider sends yet.
 */
const mobile = () => "017" + Math.floor(10_000_000 + Math.random() * 89_999_999);

async function start(page: Page) {
  await page.goto("/login?as=customer&next=/account");
  await expect(page.getByLabel("Mobile number")).toBeVisible();
}

test.describe("Customer sign-in code", () => {
  test("SMS: the sandbox notice shows the code and it signs the customer in", async ({ page }) => {
    await start(page);
    await page.getByLabel("Mobile number").fill(mobile());
    await page.getByRole("button", { name: "Send code" }).click();

    const notice = page.getByTestId("sandbox-code");
    await expect(notice).toBeVisible();
    await expect(page.getByText(/We sent a code by SMS to/)).toBeVisible();
    const code = (await notice.getByText(/^\d{6}$/).textContent())!.trim();

    await page.getByLabel(/^Code/).fill(code);
    await page.getByRole("button", { name: "Verify" }).click();
    await expect(page).toHaveURL(/\/account/);
  });

  test("Email: the code goes to the address, a wrong address is refused, and the code signs in", async ({ page }) => {
    await start(page);
    await page.getByLabel("Mobile number").fill(mobile());
    await page.getByLabel("Email", { exact: true }).check();

    await page.getByLabel("Email address").fill("not-an-email");
    await page.getByRole("button", { name: "Send code" }).click();
    await expect(page.getByText("Enter a valid email address, for example name@example.com.")).toBeVisible();

    await page.getByLabel("Email address").fill("buyer@example.com");
    await page.getByRole("button", { name: "Send code" }).click();
    await expect(page.getByText(/We sent a code by email to b\*\*\*@example\.com/)).toBeVisible();
    const code = (await page.getByTestId("sandbox-code").getByText(/^\d{6}$/).textContent())!.trim();

    await page.getByLabel(/^Code/).fill(code);
    await page.getByRole("button", { name: "Verify" }).click();
    await expect(page).toHaveURL(/\/account/);
  });

  test("a wrong code is refused with a clear message", async ({ page }) => {
    await start(page);
    await page.getByLabel("Mobile number").fill(mobile());
    await page.getByRole("button", { name: "Send code" }).click();
    await expect(page.getByTestId("sandbox-code")).toBeVisible();

    await page.getByLabel(/^Code/).fill("000000");
    await page.getByRole("button", { name: "Verify" }).click();
    await expect(page.getByRole("alert").filter({ hasText: /code/i }).first()).toBeVisible();
    await expect(page).toHaveURL(/\/login/); // still on the sign-in page ("/account" appears only in its ?next= address)
  });
});
