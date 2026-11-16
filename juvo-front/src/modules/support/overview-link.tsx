"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { Headset } from "lucide-react";

/** "Help and support" tile for the account overview. A plain link: it fetches nothing (the list page loads its own data). */
export function SupportOverviewLink() {
  const t = useTranslations("support.overview");
  return (
    <Link href="/account/support" className="flex min-h-11 items-center gap-3 rounded-card border border-line bg-card p-4 transition-colors hover:border-primary" data-testid="tile-support">
      <span className="flex size-11 shrink-0 items-center justify-center rounded-full bg-primary-soft text-primary-dark [&_svg]:size-5">
        <Headset aria-hidden />
      </span>
      <span className="min-w-0">
        <span className="block text-sm text-muted">{t("label")}</span>
        <span className="block text-xl font-semibold text-strong">{t("action")}</span>
      </span>
    </Link>
  );
}
