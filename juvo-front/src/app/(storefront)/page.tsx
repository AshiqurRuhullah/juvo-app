import Image from "next/image";
import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { Banknote, Truck, ShieldCheck, Smartphone } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/surfaces";

/**
 * Storefront entry. The shop itself (categories, product cards, cart, checkout) is built in Steps 6 to 8; this page is the
 * brand front door until then, with the trust points from the SRS and the way to sign in.
 */
export default async function StorefrontHome() {
  const t = await getTranslations("storefront");

  const trust = [
    { icon: Smartphone, title: t("trust1Title"), body: t("trust1Body") },
    { icon: Truck, title: t("trust2Title"), body: t("trust2Body") },
    { icon: Banknote, title: t("trust3Title"), body: t("trust3Body") },
    { icon: ShieldCheck, title: t("trust4Title"), body: t("trust4Body") },
  ];

  return (
    <>
      <section className="relative isolate overflow-hidden bg-secondary text-accent">
        <div className="mx-auto grid max-w-7xl items-center gap-8 px-4 py-14 sm:px-6 md:grid-cols-2 md:py-20">
          <div className="space-y-5">
            <p className="text-sm font-semibold uppercase tracking-widest text-accent/80">{t("kicker")}</p>
            <h1 className="text-4xl font-semibold leading-tight text-on-primary sm:text-5xl">{t("heroTitle")}</h1>
            <p className="max-w-lg text-lg text-accent/90">{t("heroBody")}</p>
            <div className="flex flex-wrap gap-3 pt-2">
              <Button asChild size="lg">
                <Link href="/login?as=customer">{t("heroCta")}</Link>
              </Button>
              <Button asChild size="lg" variant="secondary" className="border-accent/40 bg-transparent text-accent [@media(hover:hover)]:hover:bg-accent/10">
                <a href="https://facebook.com/juvobd" target="_blank" rel="noopener noreferrer">
                  {t("heroFacebook")}
                </a>
              </Button>
            </div>
          </div>
          <div className="mx-auto w-full max-w-sm">
            <Image src="/brand/logo-original.webp" alt="Juvo BD" width={512} height={512} priority className="aspect-square w-full rounded-card object-cover shadow-pop" />
          </div>
        </div>
      </section>

      <section className="mx-auto max-w-7xl px-4 py-12 sm:px-6" aria-labelledby="trust-heading">
        <h2 id="trust-heading" className="mb-6 text-2xl font-semibold">
          {t("whyTitle")}
        </h2>
        <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {trust.map(({ icon: Icon, title, body }) => (
            <li key={title}>
              <Card className="h-full space-y-2 p-5">
                <span className="flex size-11 items-center justify-center rounded-full bg-primary-soft text-primary-dark">
                  <Icon className="size-5" aria-hidden />
                </span>
                <h3 className="font-sans text-base font-semibold">{title}</h3>
                <p className="text-sm text-muted">{body}</p>
              </Card>
            </li>
          ))}
        </ul>
      </section>
    </>
  );
}
