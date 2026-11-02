"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { Command } from "cmdk";
import { useTranslations } from "next-intl";
import { LogOut, Search, UserRound } from "lucide-react";
import { Dialog, DialogTitle, DialogDescription } from "@/components/ui/overlays";
import * as DialogPrimitive from "@radix-ui/react-dialog";
import { Button } from "@/components/ui/button";
import { useHotkey } from "@/lib/hooks";
import { flattenNav, type NavGroup } from "@/lib/nav";
import { signOut } from "./user-menu";

/**
 * Jump to any page with Ctrl+K (Cmd+K on Mac) or the search button. It lists the pages the user may open in the active branch;
 * searching orders and customers is added with those modules.
 */
export function CommandPalette({ groups, profileHref }: { groups: NavGroup[]; profileHref: string }) {
  const [open, setOpen] = React.useState(false);
  const router = useRouter();
  const t = useTranslations("shell");
  const tn = useTranslations("nav");

  const isShortcut = React.useCallback((e: KeyboardEvent) => (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k", []);
  const toggle = React.useCallback((e: KeyboardEvent) => {
    e.preventDefault();
    setOpen((o) => !o);
  }, []);
  useHotkey(isShortcut, toggle);

  const go = (href: string) => {
    setOpen(false);
    router.push(href);
  };

  return (
    <>
      <Button variant="secondary" className="hidden w-56 justify-between gap-2 px-3 text-muted lg:inline-flex" onClick={() => setOpen(true)}>
        <span className="flex items-center gap-2">
          <Search aria-hidden />
          {t("searchPlaceholder")}
        </span>
        <kbd className="rounded-md border border-line px-1.5 py-0.5 text-xs">Ctrl K</kbd>
      </Button>
      <Button variant="ghost" size="icon" className="lg:hidden" onClick={() => setOpen(true)} aria-label={t("search")}>
        <Search aria-hidden />
      </Button>

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogPrimitive.Portal>
          <DialogPrimitive.Overlay className="anim-overlay fixed inset-0 z-50 bg-black/50" />
          <DialogPrimitive.Content
            aria-describedby="palette-desc"
            className="anim-pop fixed start-1/2 top-[12dvh] z-50 w-[calc(100%-1.5rem)] max-w-xl -translate-x-1/2 overflow-hidden rounded-card border border-line bg-card shadow-pop focus:outline-none"
          >
            <DialogTitle className="sr-only">{t("search")}</DialogTitle>
            <DialogDescription id="palette-desc" className="sr-only">
              {t("searchHelp")}
            </DialogDescription>
            <Command label={t("search")}>
              <Command.Input autoFocus placeholder={t("searchPlaceholder")} className="h-14 w-full border-b border-line bg-transparent px-5 text-base text-strong outline-none placeholder:text-muted/80" />
              <Command.List className="scroll-thin max-h-[60dvh] overflow-y-auto p-2">
                <Command.Empty className="px-4 py-8 text-center text-muted">{t("noResults")}</Command.Empty>
                {groups.map((group) => (
                  <Command.Group key={group.id} heading={tn(`groups.${group.label}`)} className="[&_[cmdk-group-heading]]:px-3 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:text-muted">
                    {flattenNav([group]).map((item) => {
                      const Icon = item.icon;
                      const label = tn(`items.${item.label}`);
                      return (
                        <Command.Item
                          key={item.id}
                          value={`${label} ${item.href}`}
                          disabled={!item.ready}
                          onSelect={() => go(item.href)}
                          className="flex min-h-11 cursor-pointer items-center gap-3 rounded-control px-3 text-base text-strong data-[disabled=true]:opacity-50 data-[selected=true]:bg-sunken [&_svg]:size-5 [&_svg]:text-muted"
                        >
                          <Icon aria-hidden />
                          <span className="flex-1">{label}</span>
                          {!item.ready ? <span className="text-xs text-muted">{tn("soon")}</span> : null}
                        </Command.Item>
                      );
                    })}
                  </Command.Group>
                ))}
                <Command.Group heading={t("account")} className="[&_[cmdk-group-heading]]:px-3 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-semibold [&_[cmdk-group-heading]]:uppercase [&_[cmdk-group-heading]]:text-muted">
                  <Command.Item value={t("profile")} onSelect={() => go(profileHref)} className="flex min-h-11 cursor-pointer items-center gap-3 rounded-control px-3 text-base data-[selected=true]:bg-sunken [&_svg]:size-5 [&_svg]:text-muted">
                    <UserRound aria-hidden /> {t("profile")}
                  </Command.Item>
                  <Command.Item value={t("signOut")} onSelect={() => void signOut()} className="flex min-h-11 cursor-pointer items-center gap-3 rounded-control px-3 text-base data-[selected=true]:bg-sunken [&_svg]:size-5 [&_svg]:text-muted">
                    <LogOut aria-hidden /> {t("signOut")}
                  </Command.Item>
                </Command.Group>
              </Command.List>
            </Command>
          </DialogPrimitive.Content>
        </DialogPrimitive.Portal>
      </Dialog>
    </>
  );
}
