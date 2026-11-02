"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { ChevronsLeft, ChevronsRight, Ellipsis, Menu } from "lucide-react";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import { Dialog, DialogTitle, DialogDescription, SheetContent } from "@/components/ui/overlays";
import { Button } from "@/components/ui/button";
import { Tooltip } from "@/components/ui/overlays";
import { postJson } from "@/lib/api/post";
import { ACCOUNT_NAV, ADMIN_NAV, activeItem, flattenNav, visibleNav } from "@/lib/nav";
import { cn } from "@/lib/utils";
import { useSession } from "@/components/providers/session-provider";
import { Logo } from "./logo";
import { NavList } from "./nav-list";
import { BranchSwitcher } from "./branch-switcher";
import { CommandPalette } from "./command-palette";
import { NotificationsBell } from "./notifications-bell";
import { LanguageSwitch, ThemeToggle } from "./preferences";
import { UserMenu } from "./user-menu";

type Area = "admin" | "account";

/**
 * Sidebar layout for the admin panel and the customer account (master prompt 5.3A).
 *
 *  - Desktop (1024 px and up): fixed sidebar, expanded by default, collapsible to an icon rail (remembered in a cookie).
 *  - Tablet (768-1023 px): icon rail; the menu button opens the full menu as a drawer.
 *  - Phone (below 768 px): off-canvas drawer (swipe, outside tap, Escape), plus a bottom bar with the 4 most used pages and "More".
 *
 * The menu is built from the user's permissions in the active branch, so nobody sees what they cannot use.
 */
export function AppShell({ area, initialCollapsed, children }: { area: Area; initialCollapsed: boolean; children: React.ReactNode }) {
  const { me } = useSession();
  const t = useTranslations("shell");
  const tn = useTranslations("nav");
  const pathname = usePathname();
  const [collapsed, setCollapsed] = React.useState(initialCollapsed);
  // The drawer is open only for the page it was opened on, so navigating closes it without an effect.
  const [drawerFor, setDrawerFor] = React.useState<string | null>(null);
  const drawer = drawerFor === pathname;
  const setDrawer = (open: boolean) => setDrawerFor(open ? pathname : null);

  const groups = React.useMemo(() => visibleNav(area === "admin" ? ADMIN_NAV : ACCOUNT_NAV, me), [area, me]);
  const profileHref = area === "admin" ? "/admin/profile" : "/account";
  const home = area === "admin" ? "/admin" : "/account";

  const toggleCollapsed = () => {
    const next = !collapsed;
    setCollapsed(next);
    void postJson("/api/bff/session/preferences", { sidebar: next ? "collapsed" : "expanded" });
  };

  const bottomItems = flattenNav(groups).filter((i) => i.bottom).slice(0, 4);
  const current = activeItem(groups, pathname);

  return (
    <div className="group/shell min-h-dvh" data-collapsed={collapsed}>
      {/* Sidebar / rail (tablet and desktop) */}
      <aside
        aria-label={t("sidebar")}
        className="fixed inset-y-0 start-0 z-30 hidden w-[4.5rem] flex-col border-e border-line bg-card md:flex lg:group-data-[collapsed=false]/shell:w-72"
      >
        <div className="flex h-16 shrink-0 items-center justify-center border-b border-line px-3 lg:group-data-[collapsed=false]/shell:justify-start">
          <Link href={home} aria-label="Juvo BD" className="inline-flex min-h-11 min-w-11 items-center justify-center gap-2.5 rounded-control">
            <Logo size={40} wordmark={false} />
            <span className="hidden font-display text-xl font-semibold text-strong lg:group-data-[collapsed=false]/shell:inline">Juvo BD</span>
          </Link>
        </div>
        <div className="scroll-thin flex-1 overflow-y-auto overflow-x-hidden px-2.5 py-4">
          <NavList groups={groups} responsive />
        </div>
        <div className="hidden border-t border-line p-2.5 lg:block">
          <Tooltip content={collapsed ? t("expand") : t("collapse")} side="right">
            <Button variant="ghost" className="w-full justify-start gap-3 px-3" onClick={toggleCollapsed} aria-label={collapsed ? t("expand") : t("collapse")} aria-expanded={!collapsed}>
              {collapsed ? <ChevronsRight aria-hidden className="rtl:rotate-180" /> : <ChevronsLeft aria-hidden className="rtl:rotate-180" />}
              <span className="hidden lg:group-data-[collapsed=false]/shell:inline">{t("collapse")}</span>
            </Button>
          </Tooltip>
        </div>
      </aside>

      {/* Content column */}
      <div className="flex min-h-dvh flex-col md:ps-[4.5rem] lg:group-data-[collapsed=false]/shell:ps-72">
        <header className="safe-top sticky top-0 z-20 flex h-16 items-center gap-2 border-b border-line bg-card/95 px-3 backdrop-blur sm:px-5">
          <Button variant="ghost" size="icon" className="lg:hidden" onClick={() => setDrawer(true)} aria-label={t("openMenu")} aria-haspopup="dialog">
            <Menu aria-hidden />
          </Button>
          <Link href={home} className="tap hidden shrink-0 items-center justify-center sm:inline-flex md:hidden" aria-label="Juvo BD">
            <Logo size={32} wordmark={false} />
          </Link>
          {area === "admin" ? <BranchSwitcher /> : null}
          <div className="flex-1" />
          <CommandPalette groups={groups} profileHref={profileHref} />
          <NotificationsBell />
          <span className="hidden md:flex">
            <LanguageSwitch />
            <ThemeToggle />
          </span>
          <UserMenu profileHref={profileHref} />
        </header>

        <main id="main" tabIndex={-1} className="mx-auto w-full max-w-7xl flex-1 px-4 py-6 pb-28 sm:px-6 md:pb-10">
          {children}
        </main>
      </div>

      {/* Phone bottom bar */}
      <nav aria-label={t("quickNav")} className="safe-bottom fixed inset-x-0 bottom-0 z-30 border-t border-line bg-card/95 backdrop-blur md:hidden">
        <ul className="mx-auto grid max-w-md grid-flow-col auto-cols-fr">
          {bottomItems.map((item) => {
            const Icon = item.icon;
            const active = current?.id === item.id;
            const label = tn(`items.${item.label}`);
            return (
              <li key={item.id}>
                {item.ready ? (
                  <Link href={item.href} aria-current={active ? "page" : undefined} className={cn("flex min-h-14 flex-col items-center justify-center gap-0.5 px-1 text-xs font-medium", active ? "text-primary" : "text-muted")}>
                    <Icon className="size-6" aria-hidden />
                    <span className="max-w-full truncate">{label}</span>
                  </Link>
                ) : (
                  <span aria-disabled="true" className="flex min-h-14 flex-col items-center justify-center gap-0.5 px-1 text-xs font-medium text-muted/60">
                    <Icon className="size-6" aria-hidden />
                    <span className="max-w-full truncate">{label}</span>
                  </span>
                )}
              </li>
            );
          })}
          <li>
            <button type="button" onClick={() => setDrawer(true)} className="flex min-h-14 w-full flex-col items-center justify-center gap-0.5 px-1 text-xs font-medium text-muted" aria-haspopup="dialog">
              <Ellipsis className="size-6" aria-hidden />
              {t("more")}
            </button>
          </li>
        </ul>
      </nav>

      <MenuDrawer open={drawer} onOpenChange={setDrawer} area={area} />
    </div>
  );
}

/** Off-canvas menu for phones and tablets. Focus is trapped, the page behind does not scroll, swipe left or tap outside to close. */
function MenuDrawer({ open, onOpenChange, area }: { open: boolean; onOpenChange: (open: boolean) => void; area: Area }) {
  const { me } = useSession();
  const t = useTranslations("shell");
  const groups = React.useMemo(() => visibleNav(area === "admin" ? ADMIN_NAV : ACCOUNT_NAV, me), [area, me]);
  const start = React.useRef<number | null>(null);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <SheetContent
        side="left"
        aria-describedby="drawer-desc"
        onTouchStart={(e) => {
          start.current = e.touches[0]?.clientX ?? null;
        }}
        onTouchEnd={(e) => {
          const end = e.changedTouches[0]?.clientX;
          if (start.current !== null && end !== undefined && start.current - end > 70) onOpenChange(false);
          start.current = null;
        }}
      >
        <DialogTitle className="sr-only">{t("menu")}</DialogTitle>
        <DialogDescription id="drawer-desc" className="sr-only">
          {t("menuHelp")}
        </DialogDescription>
        <div className="safe-top flex h-16 shrink-0 items-center justify-between border-b border-line ps-4 pe-2">
          <Logo size={36} />
          <DialogPrimitive.Close aria-label={t("closeMenu")} className="tap flex items-center justify-center rounded-control text-muted hover:bg-sunken">
            <ChevronsLeft className="size-5 rtl:rotate-180" aria-hidden />
          </DialogPrimitive.Close>
        </div>
        <div className="scroll-thin flex-1 overflow-y-auto px-2.5 py-4">
          <NavList groups={groups} onNavigate={() => onOpenChange(false)} />
        </div>
      </SheetContent>
    </Dialog>
  );
}
