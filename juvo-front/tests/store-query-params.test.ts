import { describe, expect, it } from "vitest";
import { activeFilterCount, parseListQuery, toSearchParams } from "@/modules/store/query-params";

describe("storefront list query", () => {
  it("round-trips filters through the address bar", () => {
    const query = parseListQuery({ sort: "price-asc", minPrice: "500", maxPrice: "2000", size: "M", colour: "Red", inStock: "1", onSale: "1", q: " dress " });
    expect(query).toMatchObject({ sort: "price-asc", minPrice: 500, maxPrice: 2000, size: "M", colour: "Red", inStock: true, onSale: true, q: "dress" });
    expect(parseListQuery(toSearchParams(query))).toEqual(query);
  });

  it("ignores unknown or malformed values instead of breaking the page", () => {
    const query = parseListQuery({ sort: "cheapest", minPrice: "abc", maxPrice: "-5", inStock: "yes" });
    expect(query).toEqual({ q: undefined, sort: undefined, minPrice: undefined, maxPrice: undefined, size: undefined, colour: undefined, brand: undefined, inStock: undefined, onSale: undefined });
  });

  it("keeps addresses short: newest sort and empty filters are left out", () => {
    expect(toSearchParams({ sort: "newest" }).toString()).toBe("");
    expect(toSearchParams({ sort: "popular", size: "L" }).toString()).toBe("sort=popular&size=L");
  });

  it("counts a price range as one filter and does not count sorting or search text", () => {
    expect(activeFilterCount({ minPrice: 1, maxPrice: 9, size: "M", sort: "popular", q: "x" })).toBe(2);
  });
});
