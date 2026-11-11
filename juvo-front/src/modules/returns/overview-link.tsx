"use client";

import Link from "next/link";
import { useTranslations } from "next-intl";
import { RotateCcw } from "lucide-react";
import { toNumber } from "@/modules/catalog/format";
import { useMyReturns } from "./api";

/** "Returns" tile for the account overview: how many returns are in progress, linking to the list. */
export function ReturnsOverviewLink() {
  const t = useTranslations("returns.overview");
  const active = useMyReturns({ filter: "active", page: 1, pageSize: 1 });
  const count = toNumber(active.data?.total);
  return (
    <Link href="/account/returns" className="flex min-h-11 items-center gap-3 rounded-card border border-line bg-card p-4 transition-colors hover:border-primary" data-testid="tile-returns">
      <span className="flex size-11 shrink-0 items-center justify-center rounded-full bg-primary-soft text-primary-dark [&_svg]:size-5">
        <RotateCcw aria-hidden />
      </span>
      <span className="min-w-0">
        <span className="block text-sm text-muted">{t("label")}</span>
        <span className="block text-xl font-semibold text-strong">{active.isPending ? "-" : t("inProgress", { count })}</span>
      </span>
    </Link>
  );
}
