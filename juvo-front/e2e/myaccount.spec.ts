import { test, expect } from "@playwright/test";
import { loadState } from "./support/env";
import { pick, sampleVariants, signInCustomer } from "./support/store";
import { signInViaBff } from "./support/ui";

/** The customer account: profile and consent, addresses (with the map pin) and the wishlist, against the real API. */
test.describe("My account", () => {
  test("the overview shows the counts and links to the new pages", async ({ page, context }) => {
    await signInCustomer(context);
    await page.goto("/account");
    await expect(page.getByTestId("account-mobile")).toBeVisible();
    await expect(page.getByTestId("tile-addresses")).toContainText("0");
    await expect(page.getByTestId("tile-orders")).toContainText("Orders");
    await expect(page.getByTestId("tile-orders")).toHaveAttribute("href", "/account/orders");
    await page.getByTestId("edit-profile-link").click();
    await expect(page).toHaveURL(/\/account\/profile/);
  });

  test("the profile is saved, a bad email is refused, and a consent switch remembers its time", async ({ page, context }) => {
    await signInCustomer(context);
    await page.goto("/account/profile");
    await expect(page.getByLabel("Mobile number")).toHaveAttribute("readonly", "");

    await page.getByLabel("Email address").fill("not-an-email");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Enter a valid email address, for example name@example.com.")).toBeVisible();

    await page.getByLabel("Full name").fill("Rahim Uddin");
    await page.getByLabel("Email address").fill("rahim@example.com");
    await page.getByRole("button", { name: "Save changes" }).click();
    await expect(page.getByText("Your profile was saved.")).toBeVisible();
    await page.reload();
    await expect(page.getByLabel("Full name")).toHaveValue("Rahim Uddin");

    const sms = page.getByRole("switch", { name: /Offers by SMS/ });
    await expect(sms).not.toBeChecked();
    await sms.click();
    await expect(sms).toBeChecked();
    await expect(page.getByText(/Last changed/).first()).toBeVisible();
    await page.reload();
    await expect(page.getByRole("switch", { name: /Offers by SMS/ })).toBeChecked();
  });

  test("an address with a map pin is added, made default, edited and deleted", async ({ page, context }) => {
    await signInCustomer(context);
    await page.goto("/account/addresses");
    await expect(page.getByText("No saved addresses")).toBeVisible();

    await page.getByTestId("add-address").first().click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel(/Receiver's name/).fill("Karim Hossain");
    await dialog.getByLabel(/Phone number/).fill("01712345678");

    // Missing district and landmark are explained, not just marked red.
    await dialog.getByRole("button", { name: "Save address" }).click();
    await expect(dialog.getByText("Choose a district.")).toBeVisible();
    await expect(dialog.getByText("Add a nearby landmark so the rider can find you.")).toBeVisible();

    await pick(page, dialog.getByLabel("Division"), "Dhaka");
    await pick(page, dialog.getByLabel(/^District/), "Dhaka");
    await dialog.getByLabel(/Street address/).fill("House 12, Road 5, Block C");
    await dialog.getByLabel(/Landmark/).fill("Next to the mosque");

    await dialog.getByTestId("pin-map").click({ position: { x: 120, y: 100 } });
    await expect(dialog.getByTestId("pin-coordinates")).toBeVisible();

    await dialog.getByRole("button", { name: "Save address" }).click();
    await expect(page.getByText("Address saved.")).toBeVisible();

    const cards = page.getByTestId("address-card");
    await expect(cards).toHaveCount(1);
    await expect(cards.first()).toContainText("Karim Hossain");
    await expect(cards.first()).toContainText("Default"); // the first address becomes the default
    await expect(cards.first()).toContainText("Map pin saved");

    // A second address; making it default moves the badge.
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await page.getByTestId("add-address").first().click();
    const second = page.getByRole("dialog");
    await second.getByLabel(/Receiver's name/).fill("Salma Akter");
    await second.getByLabel(/Phone number/).fill("01812345678");
    await pick(page, second.getByLabel("Division"), "Chattagram");
    await pick(page, second.getByLabel(/^District/), "Chattogram");
    await second.getByLabel(/Street address/).fill("Flat 4B, Agrabad");
    await second.getByLabel(/Landmark/).fill("Opposite the bank");
    await second.getByRole("button", { name: "Save address" }).click();
    await expect(cards).toHaveCount(2);

    await cards.filter({ hasText: "Salma Akter" }).getByRole("button", { name: "Make default" }).click();
    await expect(cards.filter({ hasText: "Salma Akter" })).toContainText("Default");
    await expect(cards.filter({ hasText: "Karim Hossain" })).not.toContainText("Default");

    await cards.filter({ hasText: "Karim Hossain" }).getByRole("button", { name: /Edit the address/ }).click();
    await page.getByRole("dialog").getByLabel(/Landmark/).fill("Beside the school");
    await page.getByRole("dialog").getByRole("button", { name: "Save address" }).click();
    await expect(cards.filter({ hasText: "Beside the school" })).toHaveCount(1);

    await cards.filter({ hasText: "Karim Hossain" }).getByRole("button", { name: /Delete the address/ }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete" }).click();
    await expect(cards).toHaveCount(1);
  });

  test("the wishlist lists saved products and moves one to the cart", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    await signInCustomer(context);
    const added = await context.request.post("/api/bff/api/v1/me/wishlist", { data: { productId: item!.productId } });
    expect(added.status(), await added.text()).toBeLessThan(300);

    await page.goto("/account/wishlist");
    const cards = page.getByTestId("wishlist-item");
    await expect(cards).toHaveCount(1);

    await cards.first().getByRole("button", { name: "Move to cart" }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel(/Colour and size/).click();
    await page.getByRole("option").first().click();
    await dialog.getByRole("button", { name: "Add to cart" }).click();
    await expect(page.getByText("Moved to your cart.")).toBeVisible();

    await page.goto("/cart");
    await expect(page.getByTestId("cart-line")).toHaveCount(1);

    // Moving a product to the cart takes it off the wishlist.
    await page.goto("/account/wishlist");
    await expect(page.getByRole("heading", { name: "Your wishlist is empty" })).toBeVisible();
  });

  test("staff are kept out of the customer pages, and signed-out visitors are sent to sign in", async ({ page, context }) => {
    await page.goto("/account/addresses");
    await expect(page).toHaveURL(/\/login/);
    await signInViaBff(context, loadState().admin.email, loadState().admin.password);
    for (const path of ["/account/addresses", "/account/wishlist", "/account/profile"]) {
      await page.goto(path);
      await expect(page).not.toHaveURL(new RegExp(`${path}$`));
    }
  });
});
