import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requireSession } from "@/lib/server/session";
import { WishlistView } from "@/modules/myaccount/wishlist-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("myaccount.wishlist");
  return { title: t("title") };
}

export default async function WishlistPage() {
  await requireSession({ subject: "customer" });
  const t = await getTranslations("myaccount.wishlist");
  return (
    <>
      <PageHeader title={t("title")} description={t("help")} />
      <WishlistView />
    </>
  );
}
