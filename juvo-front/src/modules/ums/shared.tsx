"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Search } from "lucide-react";
import type { SortingState } from "@tanstack/react-table";
import type { DataTableLabels } from "@/components/ui/data-table";
import { Input } from "@/components/ui/form";
import { Select, type SelectOption } from "@/components/ui/select";
import { useDebounced } from "@/lib/hooks";

/** Texts of the data table (paging and sorting) in the current language. */
export function useTableLabels(): DataTableLabels {
  const t = useTranslations("table");
  return {
    empty: t("empty"),
    emptyDescription: t("emptyDescription"),
    previous: t("previous"),
    next: t("next"),
    pageOf: (page, pages) => t("pageOf", { page, pages }),
    total: (total) => t("total", { count: total }),
    sortBy: (column) => t("sortBy", { column }),
  };
}

/** "name" ascending, "-name" descending: the format of the API's `Sort` parameter. */
export function toSortParam(sorting: SortingState): string | undefined {
  const first = sorting[0];
  return first ? `${first.desc ? "-" : ""}${first.id}` : undefined;
}

/**
 * State of a server-side list: page, search text (sent after a short pause so every keystroke is not a request), sorting and
 * extra filters. Changing the search, sorting or a filter returns to page 1.
 */
export function useListState<F extends Record<string, string | undefined>>(initialFilters: F, pageSize = 20, initialSort?: SortingState) {
  const [page, setPage] = React.useState(1);
  const [searchText, setSearchText] = React.useState("");
  const [sorting, setSortingState] = React.useState<SortingState>(initialSort ?? []);
  const [filters, setFiltersState] = React.useState<F>(initialFilters);
  const search = useDebounced(searchText.trim(), 350);

  const lastSearch = React.useRef(search);
  React.useEffect(() => {
    if (lastSearch.current !== search) {
      lastSearch.current = search;
      setPage(1);
    }
  }, [search]);

  return {
    page,
    setPage,
    pageSize,
    searchText,
    setSearchText,
    search,
    sorting,
    setSorting: (updater: SortingState | ((old: SortingState) => SortingState)) => {
      setSortingState(updater);
      setPage(1);
    },
    sort: toSortParam(sorting),
    filters,
    setFilter: <K extends keyof F>(key: K, value: F[K]) => {
      setFiltersState((old) => ({ ...old, [key]: value }));
      setPage(1);
    },
    resetFilters: () => {
      setFiltersState(initialFilters);
      setSearchText("");
      setPage(1);
    },
  };
}

/** Search box for list pages. */
export function SearchBox({ value, onChange, placeholder, label, className }: { value: string; onChange: (value: string) => void; placeholder: string; label: string; className?: string }) {
  const id = React.useId();
  return (
    <div className={className}>
      <label htmlFor={id} className="sr-only">
        {label}
      </label>
      <div className="relative">
        <Search className="pointer-events-none absolute start-3.5 top-1/2 size-5 -translate-y-1/2 text-muted" aria-hidden />
        <Input id={id} type="search" value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} className="ps-11" autoComplete="off" />
      </div>
    </div>
  );
}

/** A filter dropdown whose first choice is "all" (empty value). */
export function FilterSelect({
  value,
  onChange,
  options,
  allLabel,
  label,
  searchPlaceholder,
  emptyText,
}: {
  value: string | undefined;
  onChange: (value: string | undefined) => void;
  options: SelectOption[];
  allLabel: string;
  label: string;
  searchPlaceholder: string;
  emptyText: string;
}) {
  const ALL = "__all";
  const current = options.find((o) => o.value === value)?.label ?? allLabel;
  return (
    <div className="min-w-0">
      <Select
        aria-label={`${label}: ${current}`}
        value={value ?? ALL}
        onValueChange={(v) => onChange(v === ALL ? undefined : v)}
        options={[{ value: ALL, label: allLabel }, ...options]}
        placeholder={label}
        searchPlaceholder={searchPlaceholder}
        emptyText={emptyText}
      />
    </div>
  );
}

/** Date and time in the current language, or a dash. */
export function useDateTime() {
  const format = useFormatter();
  return (value: string | null | undefined, style: "short" | "long" = "short") =>
    value ? format.dateTime(new Date(value), style === "long" ? { dateStyle: "medium", timeStyle: "medium" } : { dateStyle: "medium", timeStyle: "short" }) : "-";
}
