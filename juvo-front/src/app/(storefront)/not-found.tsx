import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { SearchX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { EmptyState } from "@/components/ui/surfaces";

/** Shown inside the shop frame when a product, category or page does not exist (an old link, a removed product). */
export default async function StoreNotFound() {
  const t = await getTranslations("store.notFound");
  return (
    <div className="mx-auto max-w-2xl px-4 py-16">
      <EmptyState
        icon={<SearchX aria-hidden />}
        title={t("title")}
        description={t("body")}
        action={
          <div className="flex flex-wrap justify-center gap-3">
            <Button asChild>
              <Link href="/shop">{t("browse")}</Link>
            </Button>
            <Button asChild variant="secondary">
              <Link href="/">{t("home")}</Link>
            </Button>
          </div>
        }
      />
    </div>
  );
}
