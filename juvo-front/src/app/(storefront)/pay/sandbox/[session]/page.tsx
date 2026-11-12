import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { SandboxPayView } from "@/modules/payments/sandbox-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("payments.sandbox");
  return { title: t("title"), robots: { index: false, follow: false } };
}

const first = (value: string | string[] | undefined) => (Array.isArray(value) ? value[0] : value);

export default async function SandboxPage({ params, searchParams }: { params: Promise<{ session: string }>; searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  const { session } = await params;
  const query = await searchParams;
  return (
    <div className="mx-auto w-full max-w-xl px-4 py-8 sm:px-6 sm:py-12">
      <SandboxPayView sessionId={decodeURIComponent(session)} paymentId={first(query.payment)} />
    </div>
  );
}
