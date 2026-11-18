import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { UnsubscribeView } from "@/modules/marketing/unsubscribe-view";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("marketingStore.unsubscribe");
  return { title: t("title"), robots: { index: false, follow: false } };
}

/** `/unsubscribe?token=...`: the page the unsubscribe link of a marketing SMS or email opens. Public, no sign-in. */
export default async function UnsubscribePage({ searchParams }: { searchParams: Promise<{ token?: string | string[] }> }) {
  const { token } = await searchParams;
  const value = Array.isArray(token) ? token[0] : token;
  return <UnsubscribeView token={value && value.trim() ? value.trim() : null} />;
}
