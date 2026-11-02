"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { Tooltip } from "@/components/ui/overlays";
import { Badge } from "@/components/ui/surfaces";
import { activeItem, type NavGroup, type NavItem } from "@/lib/nav";
import { cn } from "@/lib/utils";

type Props = {
  groups: NavGroup[];
  /** Icon-only rail (tablet, or the collapsed desktop sidebar). Labels move into tooltips. */
  rail?: boolean;
  onNavigate?: () => void;
  /** Rail until the large breakpoint, labels from then on (the desktop sidebar). */
  responsive?: boolean;
};

/**
 * The menu: grouped sections with the active page highlighted in the brand colour. Items for screens that are not built yet are
 * shown muted with a "Soon" tag, never as dead links. Used by the desktop sidebar, the tablet rail and the phone drawer.
 */
export function NavList({ groups, rail = false, onNavigate, responsive = false }: Props) {
  const pathname = usePathname();
  const t = useTranslations("nav");
  const current = activeItem(groups, pathname);
  const labelClass = responsive ? "hidden lg:group-data-[collapsed=false]/shell:inline" : rail ? "sr-only" : "inline";

  return (
    <nav aria-label={t("menu")} className="space-y-5">
      {groups.map((group) => (
        <div key={group.id}>
          <h2
            className={cn(
              "px-3 pb-1.5 font-sans text-xs font-semibold uppercase tracking-wide text-muted",
              rail && !responsive && "sr-only",
              responsive && "hidden lg:group-data-[collapsed=false]/shell:block",
            )}
          >
            {t(`groups.${group.label}`)}
          </h2>
          {responsive || rail ? <div className={cn("mx-3 mb-2 h-px bg-line", responsive ? "lg:group-data-[collapsed=false]/shell:hidden" : "", !responsive && !rail && "hidden")} aria-hidden /> : null}
          <ul className="space-y-0.5">
            {group.items.map((item) => (
              <li key={item.id}>
                <NavLink item={item} active={current?.id === item.id} label={t(`items.${item.label}`)} soon={t("soon")} labelClass={labelClass} railOnly={rail || responsive} onNavigate={onNavigate} />
              </li>
            ))}
          </ul>
        </div>
      ))}
    </nav>
  );
}

function NavLink({
  item,
  active,
  label,
  soon,
  labelClass,
  railOnly,
  onNavigate,
}: {
  item: NavItem;
  active: boolean;
  label: string;
  soon: string;
  labelClass: string;
  railOnly: boolean;
  onNavigate?: () => void;
}) {
  const Icon = item.icon;
  const base =
    "group/item relative flex min-h-11 items-center gap-3 rounded-control px-3 text-base font-medium transition-colors [&_svg]:size-5 [&_svg]:shrink-0";

  const inner = (
    <>
      {active ? <span className="absolute inset-y-2 start-0 w-1 rounded-full bg-primary" aria-hidden /> : null}
      <Icon aria-hidden />
      <span className={cn("min-w-0 flex-1 truncate", labelClass)}>{label}</span>
      {!item.ready ? <Badge variant="outline" className={cn("shrink-0", labelClass === "sr-only" ? "sr-only" : labelClass)}>{soon}</Badge> : null}
    </>
  );

  const element = item.ready ? (
    <Link
      href={item.href}
      aria-current={active ? "page" : undefined}
      onClick={onNavigate}
      className={cn(base, active ? "bg-primary-soft font-semibold text-primary-dark" : "text-strong [@media(hover:hover)]:hover:bg-sunken")}
    >
      {inner}
    </Link>
  ) : (
    <span aria-disabled="true" role="link" tabIndex={-1} className={cn(base, "cursor-not-allowed text-muted")}>
      {inner}
    </span>
  );

  // In the icon rail the label is only a tooltip.
  return railOnly ? (
    <Tooltip content={item.ready ? label : `${label} (${soon})`} side="right">
      {element}
    </Tooltip>
  ) : (
    element
  );
}
