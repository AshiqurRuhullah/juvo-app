/**
 * Paging rules shared by every table and list of the system.
 *
 * Every table shows 10 rows per page. "Next" shows the next 10. The number lives here and nowhere else, so changing it changes the
 * whole system. (The storefront product grid is a grid of cards that loads more as you scroll, so it keeps its own size.)
 */
export const PAGE_SIZE = 10;

/** One slot of the page number strip: a page, or a gap that stands for hidden pages. */
export type PageSlot = number | "gap-start" | "gap-end";

/** Total number of pages for `total` rows (at least 1, so an empty list is "page 1 of 1"). */
export function pageCount(total: number, pageSize: number = PAGE_SIZE): number {
  if (!Number.isFinite(total) || total <= 0) return 1;
  return Math.max(1, Math.ceil(total / pageSize));
}

/** Keeps a page number inside 1..pages (a list that shrank must not stay on a page that no longer exists). */
export function clampPage(page: number, pages: number): number {
  if (!Number.isFinite(page)) return 1;
  return Math.min(Math.max(1, Math.floor(page)), Math.max(1, pages));
}

/** The first and last row number shown on a page, for "Showing 11 to 20 of 134". Both are 0 for an empty list. */
export function rowRange(page: number, total: number, pageSize: number = PAGE_SIZE): { from: number; to: number } {
  if (total <= 0) return { from: 0, to: 0 };
  const from = (clampPage(page, pageCount(total, pageSize)) - 1) * pageSize + 1;
  return { from, to: Math.min(total, from + pageSize - 1) };
}

/**
 * The page number strip: always the first and last page, the current page with `siblings` pages on each side, and a gap marker where
 * pages are left out. The strip always has the same number of slots once the list is long enough, so the buttons do not jump around
 * while you click through the pages. Example with 12 pages and the 6th page chosen: 1 ... 5 6 7 ... 12.
 */
export function pageWindow(current: number, pages: number, siblings = 1): PageSlot[] {
  const total = Math.max(1, Math.floor(pages));
  const page = clampPage(current, total);
  // first + last + current + two siblings + two gaps
  const slots = siblings * 2 + 5;
  if (total <= slots) return Array.from({ length: total }, (_, i) => i + 1);

  const left = Math.max(page - siblings, 1);
  const right = Math.min(page + siblings, total);
  const showStartGap = left > 3;
  const showEndGap = right < total - 2;
  const edge = siblings * 2 + 3; // pages shown next to the first or last page when only one gap is needed

  if (!showStartGap && showEndGap) {
    return [...Array.from({ length: edge }, (_, i) => i + 1), "gap-end", total];
  }
  if (showStartGap && !showEndGap) {
    return [1, "gap-start", ...Array.from({ length: edge }, (_, i) => total - edge + 1 + i)];
  }
  return [1, "gap-start", ...Array.from({ length: right - left + 1 }, (_, i) => left + i), "gap-end", total];
}
