"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useLocale, useTranslations } from "next-intl";
import { Check, Languages, Monitor, Moon, Sun } from "lucide-react";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/overlays";
import { Button } from "@/components/ui/button";
import { postJson } from "@/lib/api/post";

type Theme = "light" | "dark" | "system";

function readTheme(): Theme {
  const current = document.documentElement.getAttribute("data-theme");
  return current === "light" || current === "dark" ? current : "system";
}

function subscribeTheme(notify: () => void) {
  const observer = new MutationObserver(notify);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  return () => observer.disconnect();
}

function applyTheme(theme: Theme) {
  if (theme === "system") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);
}

/** The three theme choices as menu items (shared by the theme menu and the phone account menu). */
export function ThemeMenuItems() {
  const t = useTranslations("common");
  const theme = React.useSyncExternalStore(subscribeTheme, readTheme, () => "system" as Theme);

  const choose = (next: Theme) => {
    applyTheme(next);
    void postJson("/api/bff/session/preferences", { theme: next });
  };

  const options: { id: Theme; label: string; icon: React.ReactNode }[] = [
    { id: "light", label: t("themeLight"), icon: <Sun aria-hidden /> },
    { id: "dark", label: t("themeDark"), icon: <Moon aria-hidden /> },
    { id: "system", label: t("themeSystem"), icon: <Monitor aria-hidden /> },
  ];

  return (
    <>
      {options.map((o) => (
        <DropdownMenuItem key={o.id} onSelect={() => choose(o.id)}>
          {o.icon}
          <span className="flex-1">{o.label}</span>
          {theme === o.id ? <Check className="!text-primary" aria-hidden /> : null}
        </DropdownMenuItem>
      ))}
    </>
  );
}

/** Light, dark or follow the device. The choice is a cookie, so the server renders the right theme on the first paint. */
export function ThemeToggle() {
  const t = useTranslations("common");
  const theme = React.useSyncExternalStore(subscribeTheme, readTheme, () => "system" as Theme);
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" aria-label={t("theme")}>
          {theme === "dark" ? <Moon aria-hidden /> : theme === "light" ? <Sun aria-hidden /> : <Monitor aria-hidden />}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="min-w-44">
        <ThemeMenuItems />
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** English / Bengali as menu items. Re-renders the server components in the new language without losing the page. */
export function LanguageMenuItems() {
  const locale = useLocale();
  const router = useRouter();

  const choose = async (next: "en" | "bn") => {
    if (next === locale) return;
    await postJson("/api/bff/session/preferences", { locale: next });
    router.refresh();
  };

  return (
    <>
      {(
        [
          ["en", "English"],
          ["bn", "বাংলা"],
        ] as const
      ).map(([id, label]) => (
        <DropdownMenuItem key={id} lang={id} onSelect={() => choose(id)}>
          <span className="flex-1">{label}</span>
          {locale === id ? <Check className="!text-primary" aria-hidden /> : null}
        </DropdownMenuItem>
      ))}
    </>
  );
}

export function LanguageSwitch() {
  const t = useTranslations("common");
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" size="icon" aria-label={t("language")}>
          <Languages aria-hidden />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent className="min-w-44">
        <LanguageMenuItems />
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
