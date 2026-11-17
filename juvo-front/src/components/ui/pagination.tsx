"use client";

import * as React from "react";
import { ChevronLeft, ChevronRight, Ellipsis } from "lucide-react";
import { clampPage, pageCount, pageWindow, PAGE_SIZE, rowRange } from "@/lib/pagination";
import { cn } from "@/lib/utils";

/** The texts of the pager in the current language. `showing`, `goToPage` and `currentPage` fall back to simpler wording when missing. */
export type PaginationLabels = {
  /** Name of the navigation landmark, for screen readers. */
  nav: string;
  previous: string;
  next: string;
  pageOf: (page: number, pages: number) => string;
  /** "Showing 11 to 20 of 134". Falls back to `total`. */
  showing?: (from: number, to: number, total: number) => string;
  total: (total: number) => string;
  /** Name of a page button, for screen readers: "Go to page 4". */
  goToPage?: (page: number) => string;
  /** Name of the current page button: "Page 4, current page". */
  currentPage?: (page: number) => string;
};

type Props = {
  page: number;
  total: number;
  onPageChange: (page: number) => void;
  labels: PaginationLabels;
  pageSize?: number;
  className?: string;
};

const step =
  "inline-flex min-h-11 min-w-11 select-none items-center justify-center rounded-control px-3 text-sm font-medium tabular-nums transition-colors " +
  "focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-[var(--brand-primary-glow)] disabled:pointer-events-none disabled:opacity-40";

/** Previous and Next: a bordered button on phones, flat inside the rounded group from 640 px up. */
const edge = "border border-line bg-card text-strong shadow-sm hover:bg-sunken sm:border-0 sm:bg-transparent sm:shadow-none";

/**
 * The pager under every table. It shows which rows you are looking at ("Showing 11 to 20 of 134"), the page numbers with gaps for the
 * pages left out, and Previous and Next. The current page is the one place the brand red is used here; the rest stays calm.
 * Below 640 px the numbers give way to "Page 2 of 12" between large Previous and Next buttons, so nothing scrolls sideways.
 * With one page only the summary is shown, and with no rows the pager is not drawn at all.
 */
export function Pagination({ page, total, onPageChange, labels, pageSize = PAGE_SIZE, className }: Props) {
  const root = React.useRef<HTMLElement>(null);
  const pages = pageCount(total, pageSize);
  const current = clampPage(page, pages);
  const { from, to } = rowRange(current, total, pageSize);

  const go = (next: number) => {
    const target = clampPage(next, pages);
    if (target === current) return;
    onPageChange(target);
    // When the pager is used at the bottom of a long table, bring the top of the table back into view so the new rows are seen.
    const scope = root.current?.parentElement;
    if (scope && scope.getBoundingClientRect().top < 0) scope.scrollIntoView({ block: "start", behavior: "smooth" });
  };

  if (total <= 0) return null;

  const summary = labels.showing ? labels.showing(from, to, total) : labels.total(total);
  const slots = pageWindow(current, pages);

  return (
    <nav ref={root} aria-label={labels.nav} data-testid="pagination" className={cn("flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between", className)}>
      <p className="text-sm text-muted tabular-nums" aria-live="polite" data-testid="pagination-summary">
        {summary}
      </p>

      {pages > 1 ? (
        <div className="flex w-full items-center justify-between gap-2 sm:w-auto sm:justify-start sm:gap-1 sm:rounded-control sm:border sm:border-line sm:bg-card sm:p-1 sm:shadow-sm">
          <button type="button" onClick={() => go(current - 1)} disabled={current <= 1} data-testid="page-prev" aria-label={labels.previous} className={cn(step, edge, "sm:gap-1 sm:pe-4")}>
            <ChevronLeft aria-hidden className="size-5 rtl:rotate-180" />
            <span className="hidden sm:inline">{labels.previous}</span>
          </button>

          {/* Phones: one line of text. Tablets and desktops: the numbers. */}
          <span className="text-sm font-medium text-strong tabular-nums sm:hidden" data-testid="page-of">
            {labels.pageOf(current, pages)}
          </span>
          <ol className="hidden items-center gap-1 sm:flex" aria-label={labels.pageOf(current, pages)}>
            {slots.map((slot) =>
              typeof slot === "number" ? (
                <li key={slot}>
                  <button
                    type="button"
                    onClick={() => go(slot)}
                    data-testid={`page-${slot}`}
                    aria-current={slot === current ? "page" : undefined}
                    aria-label={slot === current ? (labels.currentPage ? labels.currentPage(slot) : labels.pageOf(slot, pages)) : labels.goToPage ? labels.goToPage(slot) : String(slot)}
                    className={cn(step, slot === current ? "bg-primary text-on-primary shadow-sm" : "text-strong hover:bg-sunken")}
                  >
                    {slot}
                  </button>
                </li>
              ) : (
                <li key={slot} aria-hidden="true" className="flex size-11 items-center justify-center text-muted">
                  <Ellipsis className="size-5" />
                </li>
              ),
            )}
          </ol>

          <button type="button" onClick={() => go(current + 1)} disabled={current >= pages} data-testid="page-next" aria-label={labels.next} className={cn(step, edge, "sm:gap-1 sm:ps-4")}>
            <span className="hidden sm:inline">{labels.next}</span>
            <ChevronRight aria-hidden className="size-5 rtl:rotate-180" />
          </button>
        </div>
      ) : null}
    </nav>
  );
}
