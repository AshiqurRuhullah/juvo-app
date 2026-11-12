import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PayView } from "@/modules/payments/pay-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("payments.pay");
  return { title: t("title"), robots: { index: false, follow: false } };
}

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value);

export default async function PayPage({ params, searchParams }: { params: Promise<{ number: string }>; searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const { number } = await params;
  const query = await searchParams;
  return (
    <div className="mx-auto w-full max-w-xl px-4 py-8 sm:px-6 sm:py-12">
      <PayView number={decodeURIComponent(number)} tokenParam={first(query.token)} retry={first(query.retry) === "1"} />
    </div>
  );
}
