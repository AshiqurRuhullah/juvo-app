"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { LogOut, UserRound } from "lucide-react";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/overlays";
import { Badge } from "@/components/ui/surfaces";
import { postJson } from "@/lib/api/post";
import { initials } from "@/lib/utils";
import { useSession } from "@/components/providers/session-provider";
import { LanguageSwitch, ThemeToggle } from "./preferences";
import { hardNavigate } from "@/lib/navigation";

export async function signOut() {
  await postJson("/api/bff/auth/logout");
  hardNavigate("/login?reason=signed-out");
}

/** Name, role summary, profile and sign out. On phones the language and theme switches live here to keep the top bar short. */
export function UserMenu({ profileHref }: { profileHref: string }) {
  const { me, name } = useSession();
  const t = useTranslations("shell");
  const roles = Array.from(new Set(me.assignments.map((a) => a.roleName)));
  const subtitle = me.staff?.email ?? me.customer?.mobile ?? "";

  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={t("accountMenu")}
        className="tap flex items-center justify-center rounded-full bg-primary-soft text-sm font-bold text-primary-dark outline-none ring-offset-0 [@media(hover:hover)]:hover:bg-primary [@media(hover:hover)]:hover:text-on-primary"
        data-testid="user-menu"
      >
        <span className="flex size-11 items-center justify-center" aria-hidden>
          {initials(name)}
        </span>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="w-72">
        <DropdownMenuLabel className="space-y-1 px-3 py-2">
          <span className="block truncate text-base font-semibold text-strong">{name}</span>
          <span className="block truncate text-sm font-normal text-muted">{subtitle}</span>
          {roles.length > 0 ? (
            <span className="mt-1 flex flex-wrap gap-1">
              {roles.slice(0, 3).map((role) => (
                <Badge key={role} variant="brand">
                  {role}
                </Badge>
              ))}
            </span>
          ) : null}
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <Link href={profileHref}>
            <UserRound aria-hidden />
            {t("profile")}
          </Link>
        </DropdownMenuItem>
        <div className="flex items-center justify-between px-3 py-1 md:hidden">
          <span className="text-sm text-muted">{t("preferences")}</span>
          <span className="flex">
            <LanguageSwitch />
            <ThemeToggle />
          </span>
        </div>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={() => void signOut()}>
          <LogOut aria-hidden />
          {t("signOut")}
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
