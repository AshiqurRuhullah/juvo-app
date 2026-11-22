import type { Metadata } from "next";
import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { getPublicBrand } from "@/modules/content/public";
import { ContactBrandPanel } from "@/modules/contact/brand-panel";
import { ContactMessageForm } from "@/modules/contact/contact-message-form";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("contact.public");
  return { title: t("title"), description: t("metaDescription"), alternates: { canonical: "/contact" } };
}

/**
 * `/contact`: the shop's general contact page. The message lands in the staff inbox (Content, Contact messages). Questions about an
 * order placed go to `/contact/order-help` (the older form that opens a support ticket).
 */
export default async function ContactPage() {
  const [t, brand] = await Promise.all([getTranslations("contact.public"), getPublicBrand()]);
  return (
    <div className="mx-auto w-full max-w-5xl px-4 py-6 sm:px-6 sm:py-10">
      <h1 className="mb-2 font-display text-2xl font-semibold text-strong sm:text-3xl">{t("title")}</h1>
      <p className="mb-6 max-w-2xl text-muted">{t("intro")}</p>
      <div className="grid gap-8 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <div className="space-y-4">
          <ContactMessageForm />
          <p className="text-sm text-muted">
            {t("orderHelpLead")}{" "}
            <Link href="/contact/order-help" className="font-medium text-primary-dark underline underline-offset-2" data-testid="order-help-page-link">
              {t("orderHelpLink")}
            </Link>
          </p>
        </div>
        <ContactBrandPanel brand={brand} />
      </div>
    </div>
  );
}
