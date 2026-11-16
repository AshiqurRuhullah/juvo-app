import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { loadSession } from "@/lib/server/session";
import { ContactFormView } from "@/modules/support/contact-form";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("support.contact");
  return { title: t("title") };
}

/** `/contact`: the guest contact form. A signed-in customer goes to the new-request page of their account (they can read the answers there). */
export default async function ContactPage() {
  const session = await loadSession();
  if (session.state === "ok" && session.me.subjectType === "customer") redirect("/account/support/new");
  const t = await getTranslations("support.contact");
  return (
    <div className="mx-auto w-full max-w-2xl px-4 py-6 sm:px-6 sm:py-10">
      <h1 className="mb-2 font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>
      <p className="mb-6 text-muted">{t("intro")}</p>
      <ContactFormView />
    </div>
  );
}
