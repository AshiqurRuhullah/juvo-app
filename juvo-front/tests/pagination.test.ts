import { describe, expect, it } from "vitest";
import { clampPage, pageCount, pageWindow, PAGE_SIZE, rowRange } from "@/lib/pagination";

describe("paging rules", () => {
  it("shows ten rows per page everywhere", () => {
    expect(PAGE_SIZE).toBe(10);
  });

  it("counts pages, with at least one for an empty list", () => {
    expect(pageCount(0)).toBe(1);
    expect(pageCount(1)).toBe(1);
    expect(pageCount(10)).toBe(1);
    expect(pageCount(11)).toBe(2);
    expect(pageCount(134)).toBe(14);
    expect(pageCount(-5)).toBe(1);
    expect(pageCount(Number.NaN)).toBe(1);
    expect(pageCount(25, 5)).toBe(5);
  });

  it("keeps the page inside the list when the list shrinks", () => {
    expect(clampPage(5, 3)).toBe(3);
    expect(clampPage(0, 3)).toBe(1);
    expect(clampPage(-2, 3)).toBe(1);
    expect(clampPage(2.9, 3)).toBe(2);
    expect(clampPage(Number.NaN, 3)).toBe(1);
    expect(clampPage(7, 0)).toBe(1);
  });

  it("works out which rows a page shows", () => {
    expect(rowRange(1, 0)).toEqual({ from: 0, to: 0 });
    expect(rowRange(1, 7)).toEqual({ from: 1, to: 7 });
    expect(rowRange(1, 134)).toEqual({ from: 1, to: 10 });
    expect(rowRange(2, 134)).toEqual({ from: 11, to: 20 });
    expect(rowRange(14, 134)).toEqual({ from: 131, to: 134 });
    expect(rowRange(99, 134)).toEqual({ from: 131, to: 134 }); // a page past the end is the last page
  });
});

describe("page number strip", () => {
  it("lists every page when there are few", () => {
    expect(pageWindow(1, 1)).toEqual([1]);
    expect(pageWindow(1, 3)).toEqual([1, 2, 3]);
    expect(pageWindow(4, 7)).toEqual([1, 2, 3, 4, 5, 6, 7]);
  });

  it("puts a gap in the middle of a long list", () => {
    expect(pageWindow(6, 12)).toEqual([1, "gap-start", 5, 6, 7, "gap-end", 12]);
    expect(pageWindow(5, 12)).toEqual([1, "gap-start", 4, 5, 6, "gap-end", 12]);
  });

  it("keeps the first pages together near the start and the last pages near the end", () => {
    expect(pageWindow(1, 12)).toEqual([1, 2, 3, 4, 5, "gap-end", 12]);
    expect(pageWindow(3, 12)).toEqual([1, 2, 3, 4, 5, "gap-end", 12]);
    expect(pageWindow(4, 12)).toEqual([1, 2, 3, 4, 5, "gap-end", 12]);
    expect(pageWindow(9, 12)).toEqual([1, "gap-start", 8, 9, 10, 11, 12]);
    expect(pageWindow(12, 12)).toEqual([1, "gap-start", 8, 9, 10, 11, 12]);
  });

  it("always has the same number of slots once the list is long, so the buttons do not jump while you click", () => {
    for (let pages = 8; pages <= 60; pages += 7) {
      for (let page = 1; page <= pages; page++) {
        expect(pageWindow(page, pages), `${page} of ${pages}`).toHaveLength(7);
      }
    }
  });

  it("always includes the current page, the first page and the last page, in order, without repeats", () => {
    for (const pages of [8, 9, 12, 25, 100]) {
      for (let page = 1; page <= pages; page++) {
        const slots = pageWindow(page, pages);
        const numbers = slots.filter((s): s is number => typeof s === "number");
        expect(numbers).toContain(page);
        expect(numbers[0]).toBe(1);
        expect(numbers[numbers.length - 1]).toBe(pages);
        expect([...numbers].sort((a, b) => a - b)).toEqual(numbers);
        expect(new Set(numbers).size).toBe(numbers.length);
      }
    }
  });

  it("copes with an out-of-range page", () => {
    expect(pageWindow(50, 12)).toEqual(pageWindow(12, 12));
    expect(pageWindow(-3, 12)).toEqual(pageWindow(1, 12));
  });
});
