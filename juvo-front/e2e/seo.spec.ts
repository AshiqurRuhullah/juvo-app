import { test, expect } from "@playwright/test";
import { resetContent, publishPage } from "./support/content";
import { sampleVariants } from "./support/store";

/**
 * Search-engine basics in the browser against the real API (FR-R01..R03): robots.txt, sitemap.xml, product metadata, social card
 * and structured data, and noindex on private pages. Reading only, except the content-page publish check, which resets after itself.
 */
test.afterAll(async () => {
  await resetContent();
});

const metaContent = async (page: import("@playwright/test").Page, selector: string) => page.locator(selector).first().getAttribute("content");

test.describe("SEO basics", () => {
  test("robots.txt opens the shop, closes private routes and names the sitemap", async ({ request }) => {
    const response = await request.get("/robots.txt");
    expect(response.status()).toBe(200);
    const text = await response.text();
    expect(text).toContain("User-Agent: *");
    expect(text).toContain("Allow: /");
    for (const path of ["/admin", "/account", "/cart", "/checkout", "/api", "/login"]) expect(text).toContain(`Disallow: ${path}`);
    expect(text).toMatch(/Sitemap: https?:\/\/[^\s]+\/sitemap\.xml/);
  });

  test("sitemap.xml lists a sample product and the public pages, never private routes", async ({ request }) => {
    await resetContent();
    const [sample] = await sampleVariants(request, 1);
    const response = await request.get("/sitemap.xml");
    expect(response.status()).toBe(200);
    const xml = await response.text();
    expect(xml).toContain(`/product/${sample.slug}</loc>`);
    expect(xml).toMatch(/<loc>https?:\/\/[^<]+\/shop<\/loc>/);
    expect(xml).toMatch(/<loc>https?:\/\/[^<]+\/contact<\/loc>/);
    for (const path of ["/admin", "/cart", "/checkout", "/account", "/login", "/api", "/search"]) expect(xml).not.toMatch(new RegExp(`<loc>[^<]*${path}[/<]`));
  });

  test("an unpublished content page is not in the sitemap and publishing it adds it", async ({ request }) => {
    await resetContent();
    const before = await (await request.get("/sitemap.xml")).text();
    expect(before).not.toMatch(/<loc>[^<]+\/faq<\/loc>/);
    await publishPage("faq", "Questions", "Ask us anything.");
    const after = await (await request.get("/sitemap.xml")).text();
    expect(after).toMatch(/<loc>[^<]+\/faq<\/loc>/);
    expect(after).toMatch(/<loc>[^<]+\/faq<\/loc>\s*<lastmod>/);
  });

  test("a product page has its own title, canonical, social card and valid structured data", async ({ page, request }) => {
    const [sample] = await sampleVariants(request, 1);
    const detail = (await (await request.get(`/api/bff/api/v1/store/products/${sample.slug}`)).json()) as {
      card: { nameEn: string; price: number | string };
      variants: { inStock: boolean }[];
    };
    await page.goto(`/product/${sample.slug}`);
    await expect(page).toHaveTitle(new RegExp(detail.card.nameEn.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
    expect(await metaContent(page, 'meta[property="og:title"]')).toBeTruthy();
    expect(await metaContent(page, 'meta[property="og:image"]')).toMatch(/^https?:\/\/[^/]+\/(media|brand)\//);
    expect(await metaContent(page, 'meta[name="twitter:card"]')).toBe("summary_large_image");
    expect(await page.locator('link[rel="canonical"]').getAttribute("href")).toMatch(new RegExp(`^https?://[^/]+/product/${sample.slug}$`));

    const blocks = await page.locator('script[type="application/ld+json"]').allTextContents();
    const parsed = blocks.map((b) => JSON.parse(b) as { "@type": string });
    const product = parsed.find((b) => b["@type"] === "Product") as unknown as {
      name: string;
      image: string[];
      offers: { price: number; priceCurrency: string; availability: string; url: string };
      aggregateRating?: unknown;
    };
    expect(product).toBeTruthy();
    expect(product.name).toBe(detail.card.nameEn);
    expect(product.offers.priceCurrency).toBe("BDT");
    expect(product.offers.price).toBe(Number(detail.card.price));
    expect(product.offers.availability).toBe(detail.variants.some((v) => v.inStock) ? "https://schema.org/InStock" : "https://schema.org/OutOfStock");
    expect(product.offers.url).toMatch(/^https?:\/\//);
    for (const image of product.image) expect(image).toMatch(/^https?:\/\//);
    expect(product.aggregateRating).toBeUndefined(); // the public API has no reviews, so none are claimed
    const crumbs = parsed.find((b) => b["@type"] === "BreadcrumbList") as unknown as { itemListElement: { position: number }[] };
    expect(crumbs.itemListElement.length).toBeGreaterThanOrEqual(2);
  });

  test("the home page carries Organization data", async ({ page }) => {
    await page.goto("/");
    const blocks = await page.locator('script[type="application/ld+json"]').allTextContents();
    const org = blocks.map((b) => JSON.parse(b) as { "@type": string; name: string; url: string }).find((b) => b["@type"] === "Organization");
    expect(org?.name).toBeTruthy();
    expect(org?.url).toMatch(/^https?:\/\//);
    expect(await page.locator('link[rel="canonical"]').getAttribute("href")).toMatch(/^https?:\/\/[^/]+\/?$/);
  });

  test("private pages are marked noindex", async ({ page }) => {
    for (const path of ["/cart", "/checkout", "/login", "/search?q=shirt", "/track"]) {
      await page.goto(path);
      const robots = await page.locator('meta[name="robots"]').first().getAttribute("content");
      expect(robots, path).toContain("noindex");
    }
  });
});
