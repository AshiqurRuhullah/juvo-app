import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { CartView } from "@/modules/cart/cart-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("cart");
  return { title: t("title"), robots: { index: false, follow: false } };
}

export default async function CartPage() {
  const t = await getTranslations("cart");
  return (
    <div className="mx-auto w-full max-w-6xl px-4 py-6 sm:px-6 sm:py-10">
      <h1 className="mb-6 font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>
      <CartView />
    </div>
  );
}
