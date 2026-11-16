import Link from "next/link";
import { ArrowRight, Compass } from "lucide-react";
import { getTranslations } from "next-intl/server";
import { Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState } from "@/components/ui/surfaces";
import { ADMIN_NAV, flattenNav, visibleNav } from "@/lib/nav";
import type { AccessView } from "@/lib/auth/permissions";

/** For people without the dashboard permission: no numbers, only the screens they may open. Nothing here is invented. */
export async function QuickLinks({ access }: { access: AccessView }) {
  const t = await getTranslations("reports.quickLinks");
  const tn = await getTranslations("nav");
  const items = flattenNav(visibleNav(ADMIN_NAV, access)).filter((i) => i.ready && i.id !== "dashboard");
  return (
    <Card data-testid="quick-links">
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("subtitle")}</CardDescription>
      </CardHeader>
      <CardContent>
        {items.length === 0 ? (
          <EmptyState icon={<Compass />} title={t("emptyTitle")} description={t("emptyText")} />
        ) : (
          <ul className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
            {items.map((item) => {
              const Icon = item.icon;
              return (
                <li key={item.id}>
                  <Link href={item.href} className="flex min-h-14 items-center gap-3 rounded-control border border-line px-3 py-2 [@media(hover:hover)]:hover:bg-sunken/60">
                    <Icon className="size-5 shrink-0 text-muted" aria-hidden />
                    <span className="min-w-0 flex-1 text-strong">{tn(`items.${item.label}`)}</span>
                    <ArrowRight className="size-4 shrink-0 text-muted rtl:rotate-180" aria-hidden />
                  </Link>
                </li>
              );
            })}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}
