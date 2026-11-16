import type { ColumnDef } from "@tanstack/react-table";

/** Small helpers for report tables: one text column (the card title on phones) and numeric columns (right aligned, tabular figures). */

export function textCol<T>(id: string, header: string, cell: (row: T) => React.ReactNode, options: { primary?: boolean; hideOnMobile?: boolean } = {}): ColumnDef<T, unknown> {
  return {
    id,
    header,
    enableSorting: false,
    meta: { primary: options.primary, secondary: !options.primary && !options.hideOnMobile, hideOnMobile: options.hideOnMobile },
    cell: ({ row }) => cell(row.original),
  };
}

export function numCol<T>(id: string, header: string, cell: (row: T) => React.ReactNode, options: { hideOnMobile?: boolean } = {}): ColumnDef<T, unknown> {
  return {
    id,
    header,
    enableSorting: false,
    meta: { numeric: true, secondary: !options.hideOnMobile, hideOnMobile: options.hideOnMobile },
    cell: ({ row }) => <span className="tabular-nums">{cell(row.original)}</span>,
  };
}
