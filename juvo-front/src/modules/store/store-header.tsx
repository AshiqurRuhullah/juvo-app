"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { ChevronDown, Heart, Menu, Search, ShoppingBag, User } from "lucide-react";
import { Logo } from "@/components/layout/logo";
import { LanguageSwitch, ThemeToggle } from "@/components/layout/preferences";
import { Dialog, DialogTitle, DialogDescription, DialogTrigger, SheetContent, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/overlays";
import { cn } from "@/lib/utils";
import { useCartCount, type StoreCategory } from "./api";
import { usePickName } from "./hooks";
import { SearchBox } from "./search-box";
import { signInUrl, useStoreSession } from "./store-session";

const iconButton = "tap relative flex size-11 items-center justify-center rounded-full text-strong transition-colors hover:bg-sunken";

function CartLink() {
  const t = useTranslations("store.header");
  const count = useCartCount();
  return (
    <Link href="/cart" className={iconButton} aria-label={count > 0 ? t("cartWithCount", { count }) : t("cart")} data-testid="cart-link">
      <ShoppingBag className="size-6" aria-hidden />
      {count > 0 ? (
        <span className="absolute end-0.5 top-0.5 flex min-w-5 items-center justify-center rounded-full bg-primary px-1 text-xs font-semibold leading-5 text-on-primary" aria-hidden>
          {count > 99 ? "99+" : count}
        </span>
      ) : null}
    </Link>
  );
}

/** Desktop category bar: a top-level link, with the sub-categories in a menu when there are any. */
function CategoryBar({ categories }: { categories: StoreCategory[] }) {
  const t = useTranslations("store.header");
  const pick = usePickName();
  const pathname = usePathname();
  return (
    <nav aria-label={t("categories")} className="hidden border-t border-line lg:block">
      <ul className="mx-auto flex max-w-7xl items-center gap-1 px-6">
        <li>
          <Link href="/shop" className={cn("inline-flex min-h-11 min-w-11 items-center justify-center rounded-md px-3 text-sm font-medium hover:text-primary", pathname === "/shop" ? "text-primary" : "text-strong")}>
            {t("shopAll")}
          </Link>
        </li>
        {categories.map((category) => {
          const href = `/category/${category.slug}`;
          const current = pathname.startsWith(href);
          return (
            <li key={category.id}>
              {category.children.length > 0 ? (
                <DropdownMenu>
                  <DropdownMenuTrigger className={cn("inline-flex min-h-11 items-center gap-1 rounded-md px-3 text-sm font-medium hover:text-primary", current ? "text-primary" : "text-strong")}>
                    {pick(category.nameEn, category.nameBn)}
                    <ChevronDown className="size-4" aria-hidden />
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="start">
                    <DropdownMenuItem asChild>
                      <Link href={href}>{t("allIn", { name: pick(category.nameEn, category.nameBn) })}</Link>
                    </DropdownMenuItem>
                    {category.children.map((child) => (
                      <DropdownMenuItem key={child.id} asChild>
                        <Link href={`/category/${child.slug}`}>{pick(child.nameEn, child.nameBn)}</Link>
                      </DropdownMenuItem>
                    ))}
                  </DropdownMenuContent>
                </DropdownMenu>
              ) : (
                <Link href={href} className={cn("inline-flex min-h-11 min-w-11 items-center justify-center rounded-md px-3 text-sm font-medium hover:text-primary", current ? "text-primary" : "text-strong")}>
                  {pick(category.nameEn, category.nameBn)}
                </Link>
              )}
            </li>
          );
        })}
      </ul>
    </nav>
  );
}

/** Phone menu: a drawer from the side with the category tree, sign-in and language. */
function MenuDrawer({ categories, accountHref, signedIn }: { categories: StoreCategory[]; accountHref: string; signedIn: boolean }) {
  const t = useTranslations("store.header");
  const pick = usePickName();
  const [open, setOpen] = React.useState(false);
  const close = () => setOpen(false);
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <button type="button" className={cn(iconButton, "lg:hidden")} aria-label={t("menu")}>
          <Menu className="size-6" aria-hidden />
        </button>
      </DialogTrigger>
      <SheetContent side="left" closeLabel={t("closeMenu")}>
        <div className="border-b border-line p-4">
          <DialogTitle className="font-sans text-lg font-semibold">{t("menu")}</DialogTitle>
          <DialogDescription className="sr-only">{t("menuHelp")}</DialogDescription>
        </div>
        <nav aria-label={t("categories")} className="flex-1 overflow-y-auto p-2">
          <Link href="/shop" onClick={close} className="flex min-h-11 items-center rounded-md px-3 font-medium text-strong hover:bg-sunken">
            {t("shopAll")}
          </Link>
          {categories.map((category) => (
            <div key={category.id}>
              <Link href={`/category/${category.slug}`} onClick={close} className="flex min-h-11 items-center rounded-md px-3 font-medium text-strong hover:bg-sunken">
                {pick(category.nameEn, category.nameBn)}
              </Link>
              {category.children.map((child) => (
                <Link key={child.id} href={`/category/${child.slug}`} onClick={close} className="flex min-h-11 items-center rounded-md ps-8 pe-3 text-sm text-muted hover:bg-sunken hover:text-strong">
                  {pick(child.nameEn, child.nameBn)}
                </Link>
              ))}
            </div>
          ))}
        </nav>
        <div className="space-y-1 border-t border-line p-2">
          <Link href={accountHref} onClick={close} className="flex min-h-11 items-center gap-2 rounded-md px-3 font-medium text-strong hover:bg-sunken">
            <User className="size-5" aria-hidden /> {signedIn ? t("myAccount") : t("signIn")}
          </Link>
          <Link href="/account/wishlist" onClick={close} className="flex min-h-11 items-center gap-2 rounded-md px-3 font-medium text-strong hover:bg-sunken">
            <Heart className="size-5" aria-hidden /> {t("wishlist")}
          </Link>
          <div className="flex items-center gap-1 px-1">
            <LanguageSwitch />
            <ThemeToggle />
          </div>
        </div>
      </SheetContent>
    </Dialog>
  );
}

/** The shop header: logo, search, language and theme, wishlist, account and cart, with the category bar under it on desktops. */
export function StoreHeader({ categories, accountHref, signedIn }: { categories: StoreCategory[]; accountHref: string; signedIn: boolean }) {
  const t = useTranslations("store.header");
  const { customer } = useStoreSession();
  const pathname = usePathname();
  const [searchOpen, setSearchOpen] = React.useState(false);
  const wishlistHref = customer ? "/account/wishlist" : signInUrl("/account/wishlist");

  return (
    <header className="safe-top sticky top-0 z-30 border-b border-line bg-card/95 backdrop-blur">
      <div className="mx-auto flex h-16 max-w-7xl items-center gap-1 px-2 sm:gap-2 sm:px-6">
        <MenuDrawer categories={categories} accountHref={accountHref} signedIn={signedIn} />
        <Logo href="/" size={44} priority wordmark={false} />
        <SearchBox className="mx-3 hidden max-w-xl flex-1 md:block" />
        <div className="ms-auto flex items-center">
          <button type="button" className={cn(iconButton, "md:hidden")} aria-label={t("search")} aria-expanded={searchOpen} onClick={() => setSearchOpen((v) => !v)}>
            <Search className="size-6" aria-hidden />
          </button>
          <span className="hidden sm:flex">
            <LanguageSwitch />
            <ThemeToggle />
          </span>
          <Link href={wishlistHref} className={cn(iconButton, "hidden sm:flex")} aria-label={t("wishlist")}>
            <Heart className="size-6" aria-hidden />
          </Link>
          <Link href={accountHref} className={cn(iconButton, "hidden sm:flex")} aria-label={signedIn ? t("myAccount") : t("signIn")}>
            <User className="size-6" aria-hidden />
          </Link>
          <CartLink />
        </div>
      </div>
      {searchOpen ? (
        <div className="border-t border-line px-4 py-2 md:hidden" key={pathname}>
          <SearchBox autoFocus onNavigate={() => setSearchOpen(false)} />
        </div>
      ) : null}
      <CategoryBar categories={categories} />
    </header>
  );
}
