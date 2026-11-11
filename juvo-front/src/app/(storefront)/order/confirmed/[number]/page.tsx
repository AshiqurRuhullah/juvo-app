import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { ConfirmationView } from "@/modules/orders/confirmation-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("orders.confirmed");
  return { title: t("title"), robots: { index: false, follow: false } };
}

export default async function ConfirmedPage({ params }: { params: Promise<{ number: string }> }) {
  const { number } = await params;
  return (
    <div className="mx-auto w-full max-w-2xl px-4 py-8 sm:px-6 sm:py-12">
      <ConfirmationView number={decodeURIComponent(number)} />
    </div>
  );
}
