"use client";

import * as React from "react";
import {
  flexRender,
  getCoreRowModel,
  useReactTable,
  type ColumnDef,
  type OnChangeFn,
  type SortingState,
  type VisibilityState,
} from "@tanstack/react-table";
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState, Skeleton } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";

/**
 * Column meta used by the phone layout: `primary` columns form the card title, `secondary` ones are the lines under it, columns
 * with `hideOnMobile` are only in the table, and `actions` marks the row-action column (shown at the card's end).
 */
declare module "@tanstack/react-table" {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  interface ColumnMeta<TData, TValue> {
    primary?: boolean;
    secondary?: boolean;
    hideOnMobile?: boolean;
    actions?: boolean;
    label?: string;
    align?: "start" | "end";
  }
}

export type DataTableLabels = {
  empty: string;
  emptyDescription?: string;
  previous: string;
  next: string;
  /** "Page {page} of {pages}" */
  pageOf: (page: number, pages: number) => string;
  /** "{total} results" */
  total: (total: number) => string;
  sortBy: (column: string) => string;
};

type Props<T> = {
  columns: ColumnDef<T, unknown>[];
  data: T[];
  labels: DataTableLabels;
  loading?: boolean;
  getRowId?: (row: T) => string;
  /** Server-side paging and sorting: the API does the work, the table only shows it (SRS FR-N01). */
  page?: number;
  pageSize?: number;
  total?: number;
  onPageChange?: (page: number) => void;
  sorting?: SortingState;
  onSortingChange?: OnChangeFn<SortingState>;
  columnVisibility?: VisibilityState;
  caption: string;
  className?: string;
};

/**
 * Data table. Desktop: a real table with sortable headers and a sticky header row. Phones: each row becomes a card (primary and
 * secondary fields, actions at the end), so there is no sideways scrolling (SRS 3.5, master prompt 5.3A).
 */
export function DataTable<T>({
  columns,
  data,
  labels,
  loading,
  getRowId,
  page = 1,
  pageSize = 20,
  total,
  onPageChange,
  sorting,
  onSortingChange,
  columnVisibility,
  caption,
  className,
}: Props<T>) {
  "use no memo"; // useReactTable returns functions that React Compiler cannot memoize safely
  // eslint-disable-next-line react-hooks/incompatible-library -- opted out of memoization above; TanStack Table v8 is not compiler-safe
  const table = useReactTable({
    data,
    columns,
    getRowId: getRowId ? (row) => getRowId(row) : undefined,
    getCoreRowModel: getCoreRowModel(),
    manualSorting: true,
    manualPagination: true,
    state: { sorting: sorting ?? [], columnVisibility: columnVisibility ?? {} },
    onSortingChange,
  });

  const rows = table.getRowModel().rows;
  const pages = total !== undefined ? Math.max(1, Math.ceil(total / pageSize)) : 1;
  const hasData = rows.length > 0;

  return (
    <div className={cn("space-y-3", className)}>
      {/* Tablet and desktop */}
      <div className="scroll-thin hidden overflow-x-auto rounded-card border border-line bg-card shadow-card md:block">
        <table className="w-full border-collapse text-start text-base">
          <caption className="sr-only">{caption}</caption>
          <thead className="sticky top-0 z-10 bg-sunken text-sm text-muted">
            {table.getHeaderGroups().map((group) => (
              <tr key={group.id}>
                {group.headers.map((header) => {
                  const sort = header.column.getIsSorted();
                  const sortable = header.column.getCanSort() && !!onSortingChange;
                  const label = typeof header.column.columnDef.header === "string" ? header.column.columnDef.header : (header.column.columnDef.meta?.label ?? header.column.id);
                  return (
                    <th
                      key={header.id}
                      scope="col"
                      aria-sort={sort === "asc" ? "ascending" : sort === "desc" ? "descending" : undefined}
                      className={cn("px-4 py-3 font-semibold first:sticky first:start-0 first:bg-sunken", header.column.columnDef.meta?.align === "end" && "text-end")}
                    >
                      {header.isPlaceholder ? null : sortable ? (
                        <button
                          type="button"
                          onClick={header.column.getToggleSortingHandler()}
                          aria-label={labels.sortBy(label)}
                          className="-mx-2 inline-flex min-h-11 items-center gap-1.5 rounded-control px-2 [@media(hover:hover)]:hover:bg-card"
                        >
                          {flexRender(header.column.columnDef.header, header.getContext())}
                          {sort === "asc" ? <ArrowUp className="size-4" aria-hidden /> : sort === "desc" ? <ArrowDown className="size-4" aria-hidden /> : <ArrowUpDown className="size-4 opacity-50" aria-hidden />}
                        </button>
                      ) : (
                        flexRender(header.column.columnDef.header, header.getContext())
                      )}
                    </th>
                  );
                })}
              </tr>
            ))}
          </thead>
          <tbody aria-busy={loading || undefined}>
            {loading && !hasData
              ? Array.from({ length: 6 }, (_, i) => (
                  <tr key={i} className="border-t border-line">
                    {table.getVisibleLeafColumns().map((column) => (
                      <td key={column.id} className="px-4 py-4">
                        <Skeleton className="h-5 w-full max-w-40" />
                      </td>
                    ))}
                  </tr>
                ))
              : rows.map((row) => (
                  <tr key={row.id} className="border-t border-line [@media(hover:hover)]:hover:bg-sunken/60">
                    {row.getVisibleCells().map((cell) => (
                      <td key={cell.id} className={cn("px-4 py-3 align-middle", cell.column.columnDef.meta?.align === "end" && "text-end")}>
                        {flexRender(cell.column.columnDef.cell, cell.getContext())}
                      </td>
                    ))}
                  </tr>
                ))}
          </tbody>
        </table>
        {!loading && !hasData ? <EmptyState title={labels.empty} description={labels.emptyDescription} /> : null}
      </div>

      {/* Phones: one card per row */}
      <ul className="space-y-3 md:hidden" aria-label={caption} aria-busy={loading || undefined}>
        {loading && !hasData
          ? Array.from({ length: 4 }, (_, i) => (
              <li key={i} className="rounded-card border border-line bg-card p-4 shadow-card">
                <Skeleton className="mb-3 h-5 w-2/3" />
                <Skeleton className="mb-2 h-4 w-full" />
                <Skeleton className="h-4 w-1/2" />
              </li>
            ))
          : rows.map((row) => {
              const cells = row.getVisibleCells();
              const primary = cells.filter((c) => c.column.columnDef.meta?.primary);
              const rest = cells.filter((c) => !c.column.columnDef.meta?.primary && !c.column.columnDef.meta?.actions && !c.column.columnDef.meta?.hideOnMobile);
              const actions = cells.filter((c) => c.column.columnDef.meta?.actions);
              return (
                <li key={row.id} className="rounded-card border border-line bg-card p-4 shadow-card">
                  <div className="flex items-start justify-between gap-3">
                    <div className="min-w-0 space-y-0.5 font-semibold text-strong [overflow-wrap:anywhere]">
                      {primary.map((c) => (
                        <div key={c.id}>{flexRender(c.column.columnDef.cell, c.getContext())}</div>
                      ))}
                    </div>
                    {actions.length > 0 ? <div className="shrink-0">{actions.map((c) => <div key={c.id}>{flexRender(c.column.columnDef.cell, c.getContext())}</div>)}</div> : null}
                  </div>
                  <dl className="mt-3 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
                    {rest.map((c) => (
                      <React.Fragment key={c.id}>
                        <dt className="text-muted">{typeof c.column.columnDef.header === "string" ? c.column.columnDef.header : (c.column.columnDef.meta?.label ?? c.column.id)}</dt>
                        <dd className="min-w-0 text-end text-strong [overflow-wrap:anywhere]">{flexRender(c.column.columnDef.cell, c.getContext())}</dd>
                      </React.Fragment>
                    ))}
                  </dl>
                </li>
              );
            })}
        {!loading && !hasData ? (
          <li>
            <EmptyState title={labels.empty} description={labels.emptyDescription} />
          </li>
        ) : null}
      </ul>

      {total !== undefined && onPageChange ? (
        <nav className="flex flex-wrap items-center justify-between gap-3" aria-label={caption}>
          <p className="text-sm text-muted">{labels.total(total)}</p>
          <div className="flex items-center gap-2">
            <Button variant="secondary" size="icon" onClick={() => onPageChange(page - 1)} disabled={page <= 1} aria-label={labels.previous}>
              <ChevronLeft aria-hidden className="rtl:rotate-180" />
            </Button>
            <span className="min-w-24 text-center text-sm font-medium text-strong">{labels.pageOf(page, pages)}</span>
            <Button variant="secondary" size="icon" onClick={() => onPageChange(page + 1)} disabled={page >= pages} aria-label={labels.next}>
              <ChevronRight aria-hidden className="rtl:rotate-180" />
            </Button>
          </div>
        </nav>
      ) : null}
    </div>
  );
}
