import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PayReturnView } from "@/modules/payments/return-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("payments.return");
  return { title: t("pageTitle"), robots: { index: false, follow: false } };
}

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value);

/** The address carries only a locator (the order number, maybe a payment id). The page never reads a result from it. */
export default async function PayReturnPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const query = await searchParams;
  return (
    <div className="mx-auto w-full max-w-xl px-4 py-8 sm:px-6 sm:py-12">
      <PayReturnView number={first(query.order) ?? ""} paymentParam={first(query.payment)} />
    </div>
  );
}
