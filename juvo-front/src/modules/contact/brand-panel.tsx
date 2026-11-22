import { getLocale, getTranslations } from "next-intl/server";
import { Clock, ExternalLink, Mail, MapPin, MessageCircle, Phone } from "lucide-react";
import type { PublicBrand } from "@/modules/content/public";
import { pickText } from "@/modules/content/logic";
import { whatsAppHref } from "./logic";

const link = "inline-flex min-h-11 items-center gap-2 text-strong hover:text-primary hover:underline [overflow-wrap:anywhere]";

/**
 * The brand block beside the contact form: address, phone, WhatsApp, e-mail, opening hours from the public brand profile. Every line is
 * hidden when empty and the whole block when all are. There is no embedded map (the shop has no read-only map component); when the
 * profile has a map point it is offered as a link to OpenStreetMap.
 */
export async function ContactBrandPanel({ brand }: { brand: PublicBrand | null }) {
  if (!brand) return null;
  const [t, locale] = await Promise.all([getTranslations("contact.public.brand"), getLocale()]);
  const address = pickText(brand.address, locale);
  const hours = pickText(brand.workingHours, locale);
  const phone = brand.phone?.trim() || null;
  const email = brand.email?.trim() || null;
  const whatsApp = whatsAppHref(brand.whatsApp);
  const lat = brand.latitude === null || brand.latitude === undefined ? null : Number(brand.latitude);
  const lng = brand.longitude === null || brand.longitude === undefined ? null : Number(brand.longitude);
  const mapHref = lat !== null && lng !== null && Number.isFinite(lat) && Number.isFinite(lng) ? `https://www.openstreetmap.org/?mlat=${lat}&mlon=${lng}#map=17/${lat}/${lng}` : null;
  if (!address && !hours && !phone && !email && !whatsApp && !mapHref) return null;

  return (
    <aside aria-labelledby="contact-brand-heading" className="space-y-4 self-start rounded-card border border-line bg-card p-5" data-testid="contact-brand">
      <h2 id="contact-brand-heading" className="font-display text-xl font-semibold text-strong">
        {t("heading")}
      </h2>
      <ul className="space-y-1">
        {address ? (
          <li className="flex items-start gap-3 py-1.5">
            <MapPin className="mt-0.5 size-5 shrink-0 text-primary-dark" aria-hidden />
            <span>
              <span className="block text-sm font-semibold text-strong">{t("address")}</span>
              <span className="block whitespace-pre-line text-muted [overflow-wrap:anywhere]">{address}</span>
            </span>
          </li>
        ) : null}
        {phone ? (
          <li>
            <a className={link} href={`tel:${phone.replace(/[^\d+]/g, "")}`}>
              <Phone className="size-5 shrink-0 text-primary-dark" aria-hidden />
              <span className="sr-only">{t("phone")}: </span>
              {phone}
            </a>
          </li>
        ) : null}
        {whatsApp ? (
          <li>
            <a className={link} href={whatsApp} target="_blank" rel="noopener noreferrer">
              <MessageCircle className="size-5 shrink-0 text-primary-dark" aria-hidden />
              {t("chat")}
              <span className="sr-only">: {brand.whatsApp}</span>
            </a>
          </li>
        ) : null}
        {email ? (
          <li>
            <a className={link} href={`mailto:${email}`}>
              <Mail className="size-5 shrink-0 text-primary-dark" aria-hidden />
              <span className="sr-only">{t("email")}: </span>
              {email}
            </a>
          </li>
        ) : null}
        {hours ? (
          <li className="flex items-start gap-3 py-1.5">
            <Clock className="mt-0.5 size-5 shrink-0 text-primary-dark" aria-hidden />
            <span>
              <span className="block text-sm font-semibold text-strong">{t("hours")}</span>
              <span className="block whitespace-pre-line text-muted [overflow-wrap:anywhere]">{hours}</span>
            </span>
          </li>
        ) : null}
        {mapHref ? (
          <li>
            <a className={link} href={mapHref} target="_blank" rel="noopener noreferrer" data-testid="contact-map-link">
              <ExternalLink className="size-5 shrink-0 text-primary-dark" aria-hidden />
              {t("map")}
            </a>
          </li>
        ) : null}
      </ul>
    </aside>
  );
}
