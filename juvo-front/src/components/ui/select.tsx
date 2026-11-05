"use client";

import * as React from "react";
import { Command } from "cmdk";
import { Check, ChevronsUpDown } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger, Dialog, DialogTitle, SheetContent } from "@/components/ui/overlays";
import { useIsMobile } from "@/lib/hooks";
import { cn } from "@/lib/utils";

export type SelectOption = { value: string; label: string; hint?: string; disabled?: boolean };

type Props = {
  id?: string;
  value: string | null | undefined;
  onValueChange: (value: string) => void;
  options: SelectOption[];
  placeholder: string;
  searchPlaceholder: string;
  emptyText: string;
  disabled?: boolean;
  invalid?: boolean;
  className?: string;
  /** Show the search box only when there are many options (default: more than 7). */
  searchable?: boolean;
  /** Name for the field when no <label htmlFor> points at it (a combobox is not named by its text). */
  "aria-label"?: string;
};

/**
 * Select with search for long lists (district, upazila, country, role). Same height and border as an input (48 px).
 * On phones the list opens as a bottom sheet with large rows; on larger screens it is a popover under the field.
 */
export function Select({ id, value, onValueChange, options, placeholder, searchPlaceholder, emptyText, disabled, invalid, className, searchable, "aria-label": ariaLabel }: Props) {
  const [open, setOpen] = React.useState(false);
  const listId = React.useId();
  const isMobile = useIsMobile();
  const selected = options.find((o) => o.value === value);
  const showSearch = searchable ?? options.length > 7;

  const list = (
    <Command className="flex flex-col" filter={(itemValue, search) => (itemValue.toLowerCase().includes(search.toLowerCase().trim()) ? 1 : 0)}>
      {showSearch ? (
        <Command.Input
          placeholder={searchPlaceholder}
          className="h-12 w-full border-b border-line bg-transparent px-4 text-strong outline-none placeholder:text-muted/80"
        />
      ) : null}
      <Command.List id={listId} className="scroll-thin max-h-72 overflow-y-auto p-1 md:max-h-64">
        <Command.Empty className="px-4 py-6 text-center text-muted">{emptyText}</Command.Empty>
        {options.map((option) => (
          <Command.Item
            key={option.value}
            value={`${option.label} ${option.hint ?? ""}`}
            disabled={option.disabled}
            onSelect={() => {
              onValueChange(option.value);
              setOpen(false);
            }}
            className="flex min-h-11 cursor-pointer items-center justify-between gap-3 rounded-control px-3 text-base text-strong data-[disabled=true]:opacity-50 data-[selected=true]:bg-sunken"
          >
            <span className="min-w-0">
              <span className="block truncate">{option.label}</span>
              {option.hint ? <span className="block truncate text-sm text-muted">{option.hint}</span> : null}
            </span>
            {option.value === value ? <Check className="size-5 shrink-0 text-primary" aria-hidden /> : null}
          </Command.Item>
        ))}
      </Command.List>
    </Command>
  );

  const trigger = (extra: React.ButtonHTMLAttributes<HTMLButtonElement> = {}) => (
    <button
      id={id}
      type="button"
      role="combobox"
      aria-expanded={open}
      aria-controls={listId}
      aria-haspopup="listbox"
      aria-invalid={invalid || undefined}
      aria-label={ariaLabel}
      disabled={disabled}
      {...extra}
      className={cn(
        "flex h-12 w-full items-center justify-between gap-2 rounded-control border border-line bg-card px-4 text-start text-base transition-[border-color,box-shadow] focus-visible:border-primary focus-visible:outline-none focus-visible:ring-4 focus-visible:ring-[var(--brand-primary-glow)] disabled:cursor-not-allowed disabled:bg-sunken disabled:opacity-70 aria-[invalid=true]:border-danger",
        className,
      )}
    >
      <span className={cn("truncate", selected ? "text-strong" : "text-muted")}>{selected?.label ?? placeholder}</span>
      <ChevronsUpDown className="size-4 shrink-0 text-muted" aria-hidden />
    </button>
  );

  if (isMobile) {
    return (
      <Dialog open={open} onOpenChange={setOpen}>
        {trigger({ onClick: () => setOpen(true) })}
        <SheetContent side="bottom" aria-describedby={undefined}>
          <DialogTitle className="sr-only">{placeholder}</DialogTitle>
          {list}
        </SheetContent>
      </Dialog>
    );
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>{trigger()}</PopoverTrigger>
      <PopoverContent className="p-0">{list}</PopoverContent>
    </Popover>
  );
}
