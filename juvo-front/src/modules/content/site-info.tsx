import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { BadgeCheck, Clock, Headset, Mail, MapPin, MessageCircle, Phone, RotateCcw, Share2, ShieldCheck, Truck } from "lucide-react";
import type { PublicBadge, PublicBrand, PublicSocialLink } from "./public";
import { pickText, publicPath, type PageKey } from "./logic";

const footerLink = "inline-flex min-h-11 min-w-11 items-center gap-2 text-muted hover:text-strong hover:underline";

/** Footer links to the published pages (nothing when none is published). */
export async function FooterPageLinks({ keys }: { keys: readonly PageKey[] }) {
  if (keys.length === 0) return null;
  const t = await getTranslations("content");
  return (
    <nav aria-label={t("public.footerInfo")} className="mx-auto max-w-7xl px-4 pb-4 sm:px-6" data-testid="footer-pages">
      <ul className="flex flex-wrap gap-x-5 text-sm">
        {keys.map((key) => (
          <li key={key}>
            <Link className={footerLink} href={publicPath(key)}>
              {t(`pageNames.${key}`)}
            </Link>
          </li>
        ))}
      </ul>
    </nav>
  );
}

/** Address, phone, WhatsApp, e-mail and hours from the brand profile. Every line is hidden when empty; the whole block when all are. */
export async function FooterBrandBlock({ brand }: { brand: PublicBrand | null }) {
  if (!brand) return null;
  const [t, locale] = await Promise.all([getTranslations("content.public"), getLocale()]);
  const address = pickText(brand.address, locale);
  const hours = pickText(brand.workingHours, locale);
  const phone = brand.phone?.trim() || null;
  const whatsApp = brand.whatsApp?.trim() || null;
  const email = brand.email?.trim() || null;
  if (!address && !hours && !phone && !whatsApp && !email) return null;
  const telHref = (value: string) => `tel:${value.replace(/[^\d+]/g, "")}`;
  const waDigits = whatsApp?.replace(/\D/g, "") ?? "";
  return (
    <ul className="space-y-1 text-sm" data-testid="footer-brand">
      {address ? (
        <li className="flex items-start gap-2 py-1.5 text-muted">
          <MapPin className="mt-0.5 size-4 shrink-0" aria-hidden />
          <span>
            <span className="sr-only">{t("footerAddress")}: </span>
            <span className="whitespace-pre-line">{address}</span>
          </span>
        </li>
      ) : null}
      {phone ? (
        <li>
          <a className={footerLink} href={telHref(phone)}>
            <Phone className="size-4 shrink-0" aria-hidden />
            <span className="sr-only">{t("footerPhone")}: </span>
            {phone}
          </a>
        </li>
      ) : null}
      {whatsApp && waDigits ? (
        <li>
          <a className={footerLink} href={`https://wa.me/${waDigits}`} target="_blank" rel="noopener noreferrer">
            <MessageCircle className="size-4 shrink-0" aria-hidden />
            <span className="sr-only">{t("footerWhatsApp")}: </span>
            {whatsApp}
          </a>
        </li>
      ) : null}
      {email ? (
        <li>
          <a className={footerLink} href={`mailto:${email}`}>
            <Mail className="size-4 shrink-0" aria-hidden />
            <span className="sr-only">{t("footerEmail")}: </span>
            {email}
          </a>
        </li>
      ) : null}
      {hours ? (
        <li className="flex items-start gap-2 py-1.5 text-muted">
          <Clock className="mt-0.5 size-4 shrink-0" aria-hidden />
          <span>
            <span className="sr-only">{t("footerHours")}: </span>
            <span className="whitespace-pre-line">{hours}</span>
          </span>
        </li>
      ) : null}
    </ul>
  );
}

/** Social links (enabled ones, in display order). Opens in a new tab; the name is written out, not only an icon. */
export async function FooterSocial({ links }: { links: readonly PublicSocialLink[] | null }) {
  if (!links || links.length === 0) return null;
  const t = await getTranslations("content");
  return (
    <ul className="flex flex-wrap gap-x-4" aria-label={t("public.social")} data-testid="footer-social">
      {links.map((link) => (
        <li key={link.platform}>
          <a className={`${footerLink} text-sm`} href={link.url} target="_blank" rel="noopener noreferrer">
            {link.platform === "WhatsApp" ? <MessageCircle className="size-4 shrink-0" aria-hidden /> : <Share2 className="size-4 shrink-0" aria-hidden />}
            {t(`platform.${link.platform}`)}
          </a>
        </li>
      ))}
    </ul>
  );
}

const BADGE_ICON: Record<string, React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>> = {
  "secure-payment": ShieldCheck,
  "delivery-coverage": Truck,
  "return-policy": RotateCcw,
  "support-hours": Headset,
};

/** The row of trust badges on the home page: only enabled badges that have text; nothing at all when there are none. */
export async function TrustBadges({ badges }: { badges: readonly PublicBadge[] | null }) {
  const locale = await getLocale();
  const items = (badges ?? []).map((b) => ({ key: b.key, text: pickText(b.text, locale) })).filter((b): b is { key: string; text: string } => !!b.text);
  if (items.length === 0) return null;
  const t = await getTranslations("content.public");
  return (
    <section className="mx-auto max-w-7xl px-4 py-10 sm:px-6" aria-labelledby="trust-heading" data-testid="trust-badges">
      <h2 id="trust-heading" className="sr-only">
        {t("trustBadges")}
      </h2>
      <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        {items.map(({ key, text }) => {
          const Icon = BADGE_ICON[key] ?? BadgeCheck;
          return (
            <li key={key} data-testid={`trust-badge-${key}`} className="flex items-center gap-4 rounded-card border border-line bg-card p-5">
              <span className="flex size-11 shrink-0 items-center justify-center rounded-full bg-primary-soft text-primary-dark">
                <Icon className="size-5" aria-hidden />
              </span>
              <p className="font-medium text-strong [overflow-wrap:anywhere]">{text}</p>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
