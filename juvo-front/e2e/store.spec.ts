import { test, expect, type APIRequestContext } from "@playwright/test";
import { pick, signInCustomer } from "./support/store";

/**
 * The storefront browse pages against the real API and the sample catalogue: home shelves, category navigation, the search combobox,
 * the product grid (sort, filters, chips, load more, empty state), the product page (colour and size pickers, add to cart, wishlist,
 * recently viewed) and the friendly "not found" pages.
 */
type Card = { slug: string; nameEn: string; price: number | string; categorySlug: string; soldOut: boolean };
type Detail = { card: Card; variants: { id: string; size: string | null; colorName: string | null; inStock: boolean }[] };

async function products(request: APIRequestContext, query = "pageSize=24"): Promise<Card[]> {
  const response = await request.get(`/api/bff/api/v1/store/products?${query}`);
  expect(response.status()).toBe(200);
  return ((await response.json()) as { items: Card[] }).items;
}

async function detail(request: APIRequestContext, slug: string): Promise<Detail> {
  const response = await request.get(`/api/bff/api/v1/store/products/${slug}`);
  expect(response.status()).toBe(200);
  return (await response.json()) as Detail;
}

/** A product that can be bought: the first one with an in-stock variant. */
async function buyable(request: APIRequestContext, needSizes = false): Promise<Detail> {
  for (const card of await products(request)) {
    const d = await detail(request, card.slug);
    const sizes = new Set(d.variants.map((v) => v.size).filter(Boolean));
    if (d.variants.some((v) => v.inStock) && (!needSizes || sizes.size > 1)) return d;
  }
  throw new Error("the sample catalogue needs a buyable product");
}

test.describe("Storefront home and navigation", () => {
  test("home shows the shelves, categories and the header with search and cart", async ({ page }) => {
    await page.goto("/");
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await expect(page.getByRole("heading", { name: "New arrivals" })).toBeVisible();
    await expect(page.getByTestId("product-card").first()).toBeVisible();
    await expect(page.getByRole("heading", { name: "Shop by category" })).toBeVisible();
    await expect(page.getByRole("search")).toBeVisible();
    await expect(page.getByTestId("cart-link")).toHaveAttribute("href", "/cart");
    await expect(page.getByRole("link", { name: "facebook.com/juvobd" }).first()).toBeVisible();
  });

  test("a category tile opens that category with its products and breadcrumb", async ({ page }) => {
    await page.goto("/");
    const tile = page.getByRole("heading", { name: "Shop by category" }).locator("xpath=following::a[contains(@href,'/category/')][1]");
    const href = await tile.getAttribute("href");
    await tile.click();
    await expect(page).toHaveURL(new RegExp(`${href}$`));
    await expect(page.getByRole("navigation", { name: "Breadcrumb" })).toContainText("Home");
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  });

  test("an unknown category or product shows the friendly page, not an error", async ({ page }) => {
    for (const path of ["/category/no-such-category", "/product/no-such-product"]) {
      await page.goto(path);
      await expect(page.getByRole("heading", { name: "We could not find that" })).toBeVisible();
      await expect(page.getByRole("link", { name: "Browse the shop" })).toBeVisible();
    }
  });
});

test.describe("Search", () => {
  test("typing shows suggestions; the keyboard picks one; Enter without a pick opens all results", async ({ page, request }) => {
    const [first] = await products(request, "pageSize=1");
    const word = first!.nameEn.split(/\s+/)[0]!.slice(0, 4);
    await page.goto("/");
    const box = page.getByRole("combobox", { name: "Search products" });
    await box.fill(word);
    const list = page.getByRole("listbox", { name: "Search suggestions" });
    await expect(list.getByRole("option").first()).toBeVisible();
    await box.press("ArrowDown");
    await expect(list.getByRole("option").first()).toHaveAttribute("aria-selected", "true");
    await box.press("Escape");
    await expect(list).toHaveCount(0);

    await box.fill(word);
    await box.press("Enter");
    await expect(page).toHaveURL(new RegExp(`/search\\?q=${word}`, "i"));
    await expect(page.getByTestId("product-card").first()).toBeVisible();
  });

  test("a search with no match explains and offers the whole shop", async ({ page }) => {
    await page.goto("/search?q=zzzzqqqq");
    await expect(page.getByRole("heading", { name: /Nothing found for/ }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: "Browse all products" })).toBeVisible();
  });
});

test.describe("Product grid", () => {
  test("sorting by price keeps the order, lives in the address and survives a reload", async ({ page }) => {
    await page.goto("/shop");
    await expect(page.getByTestId("product-card").first()).toBeVisible();
    await page.getByLabel("Sort by").selectOption("price-asc");
    await expect(page).toHaveURL(/sort=price-asc/);
    await expect(page.getByLabel("Sort by")).toHaveValue("price-asc");
    await page.reload();
    await expect(page.getByLabel("Sort by")).toHaveValue("price-asc");
    const prices = await page.getByTestId("product-card").evaluateAll((cards) =>
      cards.map((c) => Number((c.querySelector("h3 ~ p span")?.textContent ?? "0").replace(/[^\d.]/g, ""))),
    );
    expect(prices.length).toBeGreaterThan(1);
    expect(prices).toEqual([...prices].sort((a, b) => a - b));
  });

  test("filters become chips that can be removed; clearing shows everything again", async ({ page }) => {
    await page.goto("/shop");
    const total = await page.getByTestId("result-count").textContent();
    await page.getByLabel("In stock").first().click();
    await expect(page).toHaveURL(/inStock=1/);
    const chip = page.getByRole("button", { name: "Remove filter In stock" });
    await expect(chip).toBeVisible();
    await chip.click();
    await expect(page).not.toHaveURL(/inStock/);
    await expect(page.getByTestId("result-count")).toHaveText(total!);
  });

  test("an impossible price range shows the empty state with a way back", async ({ page }) => {
    await page.goto("/shop?minPrice=9000000&maxPrice=9000001");
    await expect(page.getByText("No product matches these filters. Try removing one.")).toBeVisible();
    await page.getByRole("button", { name: "Clear all filters" }).last().click();
    await expect(page.getByTestId("product-card").first()).toBeVisible();
  });

  test("Load more adds the next page", async ({ page, request }) => {
    const response = await request.get("/api/bff/api/v1/store/products?pageSize=12");
    const { total } = (await response.json()) as { total: number };
    test.skip(total <= 12, "the sample catalogue has a single page");
    await page.goto("/shop");
    await expect(page.getByTestId("product-card")).toHaveCount(12);
    await page.getByRole("button", { name: "Load more" }).click();
    await expect(page.getByTestId("product-card").nth(12)).toBeVisible();
  });
});

test.describe("Product page", () => {
  test("shows name, price, pickers and adds to the cart; the badge counts it and the toast links to the cart", async ({ page, request }) => {
    const product = await buyable(request);
    await page.goto(`/product/${product.card.slug}`);
    await expect(page.getByTestId("product-name")).toHaveText(product.card.nameEn);
    await expect(page.getByTestId("product-price")).toContainText("৳");
    await expect(page.getByRole("navigation", { name: "Breadcrumb" })).toBeVisible();

    const sizes = page.getByRole("group", { name: "Size" }).getByRole("button");
    if ((await sizes.count()) > 0) await sizes.filter({ hasNot: page.locator("[aria-disabled=true]") }).first().click();
    await page.getByTestId("add-to-cart").click();
    await expect(page.getByText(/added to your cart/)).toBeVisible();
    await expect(page.getByTestId("cart-link")).toHaveAccessibleName(/Cart, 1 item/);
    await page.getByRole("button", { name: "View cart" }).click();
    await expect(page).toHaveURL(/\/cart/);
    await expect(page.getByTestId("cart-line")).toHaveCount(1);
  });

  test("a size must be chosen first when the product has several", async ({ page, request }) => {
    const product = await buyable(request, true);
    await page.goto(`/product/${product.card.slug}`);
    await page.getByTestId("add-to-cart").click();
    await expect(page.getByRole("alert").filter({ hasText: "Please choose a size." })).toBeVisible();
    await expect(page.getByTestId("cart-link")).toHaveAccessibleName("Cart");
  });

  test("the quantity stepper stays between 1 and the limit", async ({ page, request }) => {
    const product = await buyable(request);
    await page.goto(`/product/${product.card.slug}`);
    const sizes = page.getByRole("group", { name: "Size" }).getByRole("button");
    if ((await sizes.count()) > 0) await sizes.filter({ hasNot: page.locator("[aria-disabled=true]") }).first().click();
    await expect(page.getByRole("button", { name: "One less" })).toBeDisabled();
    await page.getByRole("button", { name: "One more" }).click();
    await expect(page.getByTestId("quantity")).toHaveText(/^[12]$/);
  });

  test("delivery estimate: choosing a district shows the charge and the days", async ({ page, request }) => {
    const product = await buyable(request);
    await page.goto(`/product/${product.card.slug}`);
    await pick(page, page.getByLabel("Division"), "Dhaka");
    await pick(page, page.getByLabel("District"), "Dhaka");
    const result = page.getByTestId("delivery-result");
    await expect(result).toBeVisible();
    await expect(result).toContainText(/৳|Free delivery/);
    await expect(result).toContainText(/day|Same day/);
  });

  test("a visitor who taps the heart is offered sign-in that returns to this page", async ({ page, request }) => {
    const product = await buyable(request);
    await page.goto(`/product/${product.card.slug}`);
    await page.getByRole("button", { name: /^Save .* to your wishlist$/ }).first().click();
    const link = page.getByRole("link", { name: "Sign in", exact: true }).last();
    await expect(link).toHaveAttribute("href", new RegExp(`next=${encodeURIComponent(`/product/${product.card.slug}`)}`));
  });

  test("a signed-in customer saves and removes a product with the heart", async ({ page, context, request }) => {
    const product = await buyable(request);
    await signInCustomer(context);
    await page.goto(`/product/${product.card.slug}`);
    const heart = page.getByRole("button", { name: /to your wishlist$/ }).first();
    await heart.click();
    await expect(page.getByRole("button", { pressed: true, name: /^Remove .* from your wishlist$/ }).first()).toBeVisible();
    await page.getByRole("button", { pressed: true, name: /^Remove .* from your wishlist$/ }).first().click();
    await expect(page.getByRole("button", { pressed: false, name: /^Save .* to your wishlist$/ }).first()).toBeVisible();
  });

  test("recently viewed lists the product opened before", async ({ page, request }) => {
    const list = await products(request, "pageSize=6");
    test.skip(list.length < 2, "needs two products");
    await page.goto(`/product/${list[0]!.slug}`);
    await expect(page.getByTestId("product-name")).toBeVisible();
    await page.waitForFunction(() => !!window.localStorage.getItem("juvo_recently_viewed")); // written after hydration
    await page.goto(`/product/${list[1]!.slug}`);
    const recent = page.getByTestId("recently-viewed");
    await expect(recent).toBeVisible();
    await expect(recent.getByTestId("product-card")).toContainText(list[0]!.nameEn);
  });

  test("the page carries structured data and Open Graph tags for sharing", async ({ page, request }) => {
    const product = await buyable(request);
    await page.goto(`/product/${product.card.slug}`);
    const ld = JSON.parse((await page.locator('script[type="application/ld+json"]').first().textContent())!) as { "@type": string; offers: { priceCurrency: string } };
    expect(ld["@type"]).toBe("Product");
    expect(ld.offers.priceCurrency).toBe("BDT");
    await expect(page.locator('meta[property="og:title"]')).toHaveCount(1);
  });
});

test.describe("Bengali", () => {
  test("the shop chrome switches to Bengali", async ({ page, context }) => {
    await context.request.post("/api/bff/session/preferences", { data: { locale: "bn" } });
    await page.goto("/shop");
    await expect(page.getByRole("heading", { name: "সব পণ্য" })).toBeVisible();
    await expect(page.getByLabel("সাজান")).toBeVisible();
  });
});
