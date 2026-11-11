"use client";

import * as React from "react";
import Image from "next/image";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Search, X } from "lucide-react";
import { useDebounced } from "@/lib/hooks";
import { cn } from "@/lib/utils";
import { mediaUrl, useMoney } from "@/modules/catalog/format";
import { useSuggestions } from "./api";

type Option = { key: string; label: string; href: string; kind: "product" | "category"; image?: string | null; price?: string };

/**
 * The header search: a combobox (ARIA 1.2) with live suggestions of products and categories. Arrow keys move through the list,
 * Enter opens the highlighted item or, with nothing highlighted, the full results at /search?q=…, Escape closes the list.
 */
export function SearchBox({ className, autoFocus = false, onNavigate }: { className?: string; autoFocus?: boolean; onNavigate?: () => void }) {
  const t = useTranslations("store.search");
  const router = useRouter();
  const money = useMoney();
  const listId = React.useId();
  const [text, setText] = React.useState("");
  const [open, setOpen] = React.useState(false);
  const [active, setActive] = React.useState(-1);
  const wrapper = React.useRef<HTMLDivElement>(null);
  const debounced = useDebounced(text, 250);
  const suggestions = useSuggestions(debounced);

  const options: Option[] = React.useMemo(() => {
    const data = suggestions.data;
    if (!data) return [];
    return [
      ...data.categories.map((c): Option => ({ key: `c-${c.slug}`, label: c.name, href: `/category/${c.slug}`, kind: "category" })),
      ...data.products.map((p): Option => ({ key: `p-${p.slug}`, label: p.name, href: `/product/${p.slug}`, kind: "product", image: p.imageUrl, price: money(p.price) })),
    ];
  }, [suggestions.data, money]);

  const go = (href: string) => {
    setOpen(false);
    setActive(-1);
    onNavigate?.();
    router.push(href);
  };

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    const chosen = options[active];
    if (chosen) return go(chosen.href);
    const q = text.trim();
    if (q) go(`/search?q=${encodeURIComponent(q)}`);
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === "ArrowDown" && options.length) {
      event.preventDefault();
      setOpen(true);
      setActive((i) => (i + 1) % options.length);
    } else if (event.key === "ArrowUp" && options.length) {
      event.preventDefault();
      setActive((i) => (i <= 0 ? options.length - 1 : i - 1));
    } else if (event.key === "Escape") {
      setOpen(false);
      setActive(-1);
    }
  };

  React.useEffect(() => {
    const close = (event: PointerEvent) => {
      if (!wrapper.current?.contains(event.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", close);
    return () => document.removeEventListener("pointerdown", close);
  }, []);

  const showList = open && debounced.trim().length >= 2 && text.trim().length >= 2;

  return (
    <div ref={wrapper} className={cn("relative", className)}>
      <form role="search" onSubmit={submit} className="relative">
        <label htmlFor={`${listId}-input`} className="sr-only">
          {t("label")}
        </label>
        <Search className="pointer-events-none absolute start-3.5 top-1/2 size-5 -translate-y-1/2 text-muted" aria-hidden />
        <input
          id={`${listId}-input`}
          type="search"
          role="combobox"
          aria-expanded={showList}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={active >= 0 ? `${listId}-${active}` : undefined}
          autoComplete="off"
          autoFocus={autoFocus}
          value={text}
          placeholder={t("placeholder")}
          onChange={(e) => {
            setText(e.target.value);
            setOpen(true);
            setActive(-1);
          }}
          onFocus={() => setOpen(true)}
          onKeyDown={onKeyDown}
          className="h-11 w-full rounded-full border border-line bg-sunken ps-11 pe-11 text-base text-strong placeholder:text-muted focus:border-primary focus:bg-card focus:outline-none"
        />
        {text ? (
          <button type="button" aria-label={t("clear")} onClick={() => { setText(""); setActive(-1); }} className="tap absolute end-1 top-1/2 flex size-11 -translate-y-1/2 items-center justify-center rounded-full text-muted hover:text-strong">
            <X className="size-4" aria-hidden />
          </button>
        ) : null}
      </form>

      {showList ? (
        <div className="absolute inset-x-0 top-full z-40 mt-2 overflow-hidden rounded-card border border-line bg-card shadow-pop">
          <ul id={listId} role="listbox" aria-label={t("suggestions")} className="max-h-[min(24rem,60dvh)] overflow-y-auto py-1">
            {options.map((option, index) => (
              <li
                key={option.key}
                id={`${listId}-${index}`}
                role="option"
                aria-selected={index === active}
                onPointerDown={(e) => e.preventDefault()}
                onClick={() => go(option.href)}
                onPointerEnter={() => setActive(index)}
                className={cn("flex min-h-11 cursor-pointer items-center gap-3 px-3 py-1.5 text-sm", index === active && "bg-primary-soft")}
              >
                {option.kind === "product" ? (
                  <span className="relative block size-10 shrink-0 overflow-hidden rounded-md bg-sunken">
                    {option.image ? <Image src={mediaUrl(option.image) ?? ""} alt="" fill unoptimized sizes="40px" className="object-cover" /> : null}
                  </span>
                ) : (
                  <Search className="size-4 shrink-0 text-muted" aria-hidden />
                )}
                <span className="min-w-0 flex-1 truncate text-strong">{option.label}</span>
                {option.kind === "category" ? <span className="text-xs text-muted">{t("category")}</span> : <span className="text-sm font-medium text-strong">{option.price}</span>}
              </li>
            ))}
            {suggestions.isSuccess && options.length === 0 ? <li role="presentation" className="px-4 py-3 text-sm text-muted">{t("none", { q: text.trim() })}</li> : null}
            {suggestions.isPending ? <li role="presentation" className="px-4 py-3 text-sm text-muted">{t("searching")}</li> : null}
          </ul>
          {text.trim() ? (
            <button type="button" onPointerDown={(e) => e.preventDefault()} onClick={() => go(`/search?q=${encodeURIComponent(text.trim())}`)} className="flex min-h-11 w-full items-center border-t border-line px-4 text-sm font-semibold text-primary hover:bg-sunken">
              {t("seeAll", { q: text.trim() })}
            </button>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
