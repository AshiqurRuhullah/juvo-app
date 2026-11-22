import { describe, expect, it } from "vitest";
import {
  absoluteUrl,
  availabilityUrl,
  buildBreadcrumbJsonLd,
  buildOrganizationJsonLd,
  buildProductJsonLd,
  isIndexablePath,
  jsonLdString,
  PRIVATE_PATHS,
  shopName,
  siteUrl,
} from "@/lib/seo";

describe("siteUrl", () => {
  it("falls back to localhost and never to a production domain", () => {
    expect(siteUrl({})).toBe("http://localhost:3000");
  });
  it("prefers SITE_URL, trims blanks and trailing slashes", () => {
    expect(siteUrl({ SITE_URL: " https://shop.example/ ", NEXT_PUBLIC_SITE_URL: "http://x" })).toBe("https://shop.example");
    expect(siteUrl({ NEXT_PUBLIC_SITE_URL: "http://localhost:3100//" })).toBe("http://localhost:3100");
  });
});

describe("absoluteUrl", () => {
  const base = "https://shop.example";
  it("maps API file addresses to the /media route", () => {
    expect(absoluteUrl("/files/products/a.webp", base)).toBe("https://shop.example/media/products/a.webp");
  });
  it("keeps absolute addresses and joins plain paths", () => {
    expect(absoluteUrl("https://cdn.example/x.png", base)).toBe("https://cdn.example/x.png");
    expect(absoluteUrl("/shop", base)).toBe("https://shop.example/shop");
    expect(absoluteUrl("brand/og-image.png", base)).toBe("https://shop.example/brand/og-image.png");
  });
  it("returns undefined for nothing", () => {
    expect(absoluteUrl(null, base)).toBeUndefined();
    expect(absoluteUrl("  ", base)).toBeUndefined();
  });
});

describe("indexable paths", () => {
  it("keeps private areas out", () => {
    for (const p of ["/admin", "/admin/orders", "/account/orders", "/cart", "/checkout", "/api/bff/x", "/login", "/search", "/order/confirmed/JV-1", "/pay/JV-1", "/contact/order-help"]) {
      expect(isIndexablePath(p), p).toBe(false);
    }
  });
  it("lets the shop through", () => {
    for (const p of ["/", "/shop", "/contact", "/product/blue-shirt", "/category/women/dresses", "/about", "/privacy", "/orders-and-more"]) {
      expect(isIndexablePath(p), p).toBe(true);
    }
  });
  it("lists the private routes robots.txt must close", () => {
    for (const p of ["/admin", "/account", "/cart", "/checkout", "/api", "/login"]) expect(PRIVATE_PATHS).toContain(p);
  });
});

describe("shopName", () => {
  it("uses the brand name or the neutral default", () => {
    expect(shopName(" Juvo Fashion ")).toBe("Juvo Fashion");
    expect(shopName(null)).toBe("Juvo BD");
    expect(shopName("  ")).toBe("Juvo BD");
  });
});

describe("jsonLdString", () => {
  it("escapes < so the script tag cannot be closed", () => {
    const text = jsonLdString({ name: "</script><script>alert(1)</script>" });
    expect(text).not.toContain("<");
    expect(JSON.parse(text).name).toBe("</script><script>alert(1)</script>");
  });
  it("escapes line separators", () => {
    expect(jsonLdString({ a: "x y" })).toContain("\\u2028");
  });
});

describe("product JSON-LD", () => {
  const base = { name: "Shirt", url: "https://s/product/shirt", images: ["https://s/media/a.webp"], price: "1250.00", inStock: true };
  it("builds the offer from the real price and stock", () => {
    const ld = buildProductJsonLd({ ...base, sku: "JV-1", brand: "Juvo", description: "Soft." });
    expect(ld["@type"]).toBe("Product");
    expect(ld.offers).toMatchObject({ priceCurrency: "BDT", price: 1250, availability: "https://schema.org/InStock", url: base.url });
    expect(ld.brand).toEqual({ "@type": "Brand", name: "Juvo" });
    expect(ld.sku).toBe("JV-1");
  });
  it("leaves out empty fields and the rating when there are no reviews", () => {
    const ld = buildProductJsonLd({ ...base, description: null, brand: null, sku: null, images: [] });
    expect(ld).not.toHaveProperty("description");
    expect(ld).not.toHaveProperty("brand");
    expect(ld).not.toHaveProperty("image");
    expect(ld).not.toHaveProperty("aggregateRating");
    expect(buildProductJsonLd({ ...base, rating: { value: 4.5, count: 0 } })).not.toHaveProperty("aggregateRating");
  });
  it("adds aggregateRating only for real reviews", () => {
    const ld = buildProductJsonLd({ ...base, rating: { value: 4.5, count: 12 } });
    expect(ld.aggregateRating).toEqual({ "@type": "AggregateRating", ratingValue: 4.5, reviewCount: 12 });
  });
  it("maps availability", () => {
    expect(availabilityUrl(true)).toMatch(/InStock$/);
    expect(availabilityUrl(false)).toMatch(/OutOfStock$/);
    expect(availabilityUrl(false, true)).toMatch(/PreOrder$/);
  });
});

describe("breadcrumb and organization JSON-LD", () => {
  it("numbers the breadcrumb items from 1 and is null when empty", () => {
    const ld = buildBreadcrumbJsonLd([
      { name: "Home", url: "https://s/" },
      { name: "Shirt", url: "https://s/product/shirt" },
    ]);
    expect(ld?.itemListElement.map((i) => i.position)).toEqual([1, 2]);
    expect(buildBreadcrumbJsonLd([])).toBeNull();
  });
  it("keeps only real values and https social links", () => {
    const ld = buildOrganizationJsonLd({ name: "Juvo BD", url: "https://s", logo: null, sameAs: ["https://facebook.com/juvo", "javascript:alert(1)"] });
    expect(ld).not.toHaveProperty("logo");
    expect(ld.sameAs).toEqual(["https://facebook.com/juvo"]);
    expect(buildOrganizationJsonLd({ name: "Juvo BD", url: "https://s" })).not.toHaveProperty("sameAs");
  });
});
