"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { ChevronDown } from "lucide-react";
import { Popover, PopoverContent, PopoverTrigger, Tooltip } from "@/components/ui/overlays";
import { Badge } from "@/components/ui/surfaces";
import { activeItem, isLeafGroup, type NavGroup, type NavItem } from "@/lib/nav";
import { cn } from "@/lib/utils";

/* ---- Which parents are open (remembered in the browser; works without storage too) ------------------------------------------ */

const STORAGE_KEY = "juvo_nav_open";
const listeners = new Set<() => void>();
let memory: string | null = null;

function readOpen(): string {
  if (memory !== null) return memory;
  try {
    return window.localStorage.getItem(STORAGE_KEY) ?? "";
  } catch {
    return "";
  }
}

function writeOpen(map: Record<string, boolean>) {
  const text = JSON.stringify(map);
  memory = text;
  try {
    window.localStorage.setItem(STORAGE_KEY, text);
  } catch {
    // Storage can be blocked (private window): the choice then lasts until the page is reloaded.
  }
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  window.addEventListener("storage", listener);
  return () => {
    listeners.delete(listener);
    window.removeEventListener("storage", listener);
  };
}

function useOpenGroups() {
  const raw = React.useSyncExternalStore(subscribe, readOpen, () => "");
  const map = React.useMemo<Record<string, boolean>>(() => {
    try {
      return raw ? (JSON.parse(raw) as Record<string, boolean>) : {};
    } catch {
      return {};
    }
  }, [raw]);
  const set = React.useCallback((id: string, open: boolean) => writeOpen({ ...JSON.parse(readOpen() || "{}"), [id]: open }), []);
  return { map, set };
}

/* ---- Menu tree (desktop sidebar, phone drawer) --------------------------------------------------------------------------------- */

type TreeProps = { groups: NavGroup[]; onNavigate?: () => void; className?: string };

/**
 * The menu as a tree. A group with several items is a collapsible parent (icon, label, chevron) whose children sit indented under a
 * thin guide line; a group with one item is just a link. The parent of the current page is open and tinted, the current child has the
 * soft brand tint and an accent bar. Items for screens that are not built yet are muted with a "Soon" tag, never dead links.
 */
export function NavTree({ groups, onNavigate, className }: TreeProps) {
  const pathname = usePathname();
  const t = useTranslations("nav");
  const current = activeItem(groups, pathname);
  const { map, set } = useOpenGroups();
  const flat = groups.length === 1; // the customer account menu is one list, no parent row

  return (
    <nav aria-label={t("menu")} className={cn("space-y-1", className)}>
      {groups.map((group) => {
        if (flat || isLeafGroup(group)) {
          return group.items.map((item) => (
            <NavLink key={item.id} item={item} active={current?.id === item.id} label={t(`items.${item.label}`)} soon={t("soon")} onNavigate={onNavigate} level="top" />
          ));
        }
        const containsActive = group.items.some((i) => i.id === current?.id);
        const open = map[group.id] ?? containsActive;
        const GroupIcon = group.icon;
        const panelId = `nav-group-${group.id}`;
        return (
          <div key={group.id} className="pt-0.5">
            <button
              type="button"
              aria-expanded={open}
              aria-controls={panelId}
              onClick={() => set(group.id, !open)}
              className={cn(
                "flex min-h-11 w-full items-center gap-3 rounded-control px-3 text-start text-[0.9375rem] font-medium transition-colors [&_svg]:size-5 [&_svg]:shrink-0",
                containsActive ? "text-primary-dark" : "text-strong [@media(hover:hover)]:hover:bg-sunken",
                containsActive && "bg-primary-soft/50",
              )}
            >
              <GroupIcon aria-hidden className={containsActive ? "text-primary-dark" : "text-muted"} />
              <span className="min-w-0 flex-1 truncate">{t(`groups.${group.label}`)}</span>
              <ChevronDown aria-hidden className={cn("!size-4 text-muted transition-transform duration-200", open ? "rotate-180" : "rtl:rotate-0")} />
            </button>
            <div id={panelId} className={cn("grid transition-[grid-template-rows,visibility] duration-200 ease-out", open ? "visible grid-rows-[1fr]" : "invisible grid-rows-[0fr]")} inert={!open}>
              <div className="min-h-0 overflow-hidden">
                <ul className="ms-[1.4rem] mt-1 space-y-0.5 border-s border-line pb-1 ps-3">
                  {group.items.map((item) => (
                    <li key={item.id} className="relative">
                      <NavLink item={item} active={current?.id === item.id} label={t(`items.${item.label}`)} soon={t("soon")} onNavigate={() => { set(group.id, true); onNavigate?.(); }} level="child" />
                    </li>
                  ))}
                </ul>
              </div>
            </div>
          </div>
        );
      })}
    </nav>
  );
}

function NavLink({ item, active, label, soon, onNavigate, level }: { item: NavItem; active: boolean; label: string; soon: string; onNavigate?: () => void; level: "top" | "child" }) {
  const Icon = item.icon;
  const base = cn(
    "relative flex min-h-11 items-center gap-3 rounded-control px-3 text-[0.9375rem] transition-colors [&_svg]:size-5 [&_svg]:shrink-0",
    level === "child" && "text-sm",
  );
  const inner = (
    <>
      {level === "child" && active ? <span className="absolute -start-[calc(0.75rem+1px)] bottom-2.5 top-2.5 w-0.5 rounded-full bg-primary" aria-hidden /> : null}
      {level === "top" && active ? <span className="absolute inset-y-2.5 start-0 w-1 rounded-full bg-primary" aria-hidden /> : null}
      {level === "top" ? <Icon aria-hidden className={active ? "text-primary-dark" : "text-muted"} /> : null}
      <span className="min-w-0 flex-1 truncate">{label}</span>
      {!item.ready ? <Badge variant="outline" className="shrink-0">{soon}</Badge> : null}
    </>
  );

  return item.ready ? (
    <Link
      href={item.href}
      aria-current={active ? "page" : undefined}
      onClick={onNavigate}
      className={cn(base, active ? "bg-primary-soft font-medium text-primary-dark" : "text-strong/90 [@media(hover:hover)]:hover:bg-sunken")}
    >
      {inner}
    </Link>
  ) : (
    <span aria-disabled="true" role="link" tabIndex={-1} className={cn(base, "cursor-not-allowed text-muted")}>
      {inner}
    </span>
  );
}

/* ---- Icon rail (tablet, and the collapsed desktop sidebar) ------------------------------------------------------------------------ */

/**
 * The same menu as one icon per entry. A parent opens a small list of its children next to the rail (a popover), a single link keeps a
 * tooltip. Every child stays reachable with mouse, touch and keyboard.
 */
export function NavRail({ groups, className }: { groups: NavGroup[]; className?: string }) {
  const pathname = usePathname();
  const t = useTranslations("nav");
  const current = activeItem(groups, pathname);
  const flat = groups.length === 1;

  return (
    <nav aria-label={t("menu")} className={cn("flex flex-col items-center gap-1.5", className)}>
      {groups.map((group) => {
        if (flat || isLeafGroup(group)) {
          return group.items.map((item) => <RailLink key={item.id} item={item} active={current?.id === item.id} label={t(`items.${item.label}`)} soon={t("soon")} />);
        }
        const containsActive = group.items.some((i) => i.id === current?.id);
        const GroupIcon = group.icon;
        const label = t(`groups.${group.label}`);
        return (
          <Popover key={group.id}>
            <PopoverTrigger asChild>
              <button
                type="button"
                aria-label={label}
                aria-haspopup="menu"
                className={cn(
                  "relative flex size-11 items-center justify-center rounded-control transition-colors [&_svg]:size-5",
                  containsActive ? "bg-primary-soft text-primary-dark" : "text-muted [@media(hover:hover)]:hover:bg-sunken [@media(hover:hover)]:hover:text-strong",
                )}
              >
                {containsActive ? <span className="absolute inset-y-2.5 start-0 w-1 rounded-full bg-primary" aria-hidden /> : null}
                <GroupIcon aria-hidden />
              </button>
            </PopoverTrigger>
            <PopoverContent side="right" align="start" sideOffset={10} className="w-60 min-w-0 p-2">
              <p className="px-3 pb-1.5 pt-1 text-xs font-medium tracking-wide text-muted">{label}</p>
              <ul className="space-y-0.5">
                {group.items.map((item) => (
                  <li key={item.id}>
                    {item.ready ? (
                      <Link
                        href={item.href}
                        aria-current={current?.id === item.id ? "page" : undefined}
                        className={cn("flex min-h-11 items-center gap-3 rounded-control px-3 text-sm [&_svg]:size-5", current?.id === item.id ? "bg-primary-soft font-medium text-primary-dark" : "text-strong [@media(hover:hover)]:hover:bg-sunken")}
                      >
                        <item.icon aria-hidden className={current?.id === item.id ? "text-primary-dark" : "text-muted"} />
                        <span className="min-w-0 flex-1 truncate">{t(`items.${item.label}`)}</span>
                      </Link>
                    ) : (
                      <span aria-disabled="true" role="link" tabIndex={-1} className="flex min-h-11 cursor-not-allowed items-center gap-3 rounded-control px-3 text-sm text-muted [&_svg]:size-5">
                        <item.icon aria-hidden />
                        <span className="min-w-0 flex-1 truncate">{t(`items.${item.label}`)}</span>
                        <Badge variant="outline">{t("soon")}</Badge>
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            </PopoverContent>
          </Popover>
        );
      })}
    </nav>
  );
}

function RailLink({ item, active, label, soon }: { item: NavItem; active: boolean; label: string; soon: string }) {
  const Icon = item.icon;
  const base = "relative flex size-11 items-center justify-center rounded-control transition-colors [&_svg]:size-5";
  const mark = active ? <span className="absolute inset-y-2.5 start-0 w-1 rounded-full bg-primary" aria-hidden /> : null;
  return (
    <Tooltip content={item.ready ? label : `${label} (${soon})`} side="right">
      {item.ready ? (
        <Link href={item.href} aria-label={label} aria-current={active ? "page" : undefined} className={cn(base, active ? "bg-primary-soft text-primary-dark" : "text-muted [@media(hover:hover)]:hover:bg-sunken [@media(hover:hover)]:hover:text-strong")}>
          {mark}
          <Icon aria-hidden />
        </Link>
      ) : (
        <span aria-disabled="true" role="link" aria-label={`${label} (${soon})`} tabIndex={-1} className={cn(base, "cursor-not-allowed text-muted/60")}>
          <Icon aria-hidden />
        </span>
      )}
    </Tooltip>
  );
}
