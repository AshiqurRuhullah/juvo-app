import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { TrackView } from "@/modules/orders/track-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("orders.track");
  return { title: t("title"), robots: { index: false, follow: false } };
}

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value);

export default async function TrackPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const params = await searchParams;
  const t = await getTranslations("orders.track");
  const token = first(params.token);
  const number = first(params.number);
  const mobile = first(params.mobile);
  return (
    <div className="mx-auto w-full max-w-4xl px-4 py-6 sm:px-6 sm:py-10">
      <h1 className="mb-6 font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>
      <TrackView key={`${token ?? ""}|${number ?? ""}|${mobile ?? ""}`} initialToken={token} initialNumber={number} initialMobile={mobile} />
    </div>
  );
}
