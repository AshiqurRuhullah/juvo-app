import type { Metadata, Viewport } from "next";
import { Noto_Sans_Bengali, Roboto } from "next/font/google";
import { cookies } from "next/headers";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getTranslations } from "next-intl/server";
import { Providers } from "@/components/providers/providers";
import { COOKIES } from "@/lib/auth/cookies";
import { toLocale } from "@/i18n/request";
import { loadScopedMessages } from "@/messages/scopes";
import { getPublicBrand } from "@/modules/content/public";
import { shopName, siteUrl } from "@/lib/seo";
import "./globals.css";

// Self-hosted by next/font at build time (no request to Google when a visitor opens the site). Roboto is Apache-2.0, Noto Sans Bengali is SIL OFL.
const roboto = Roboto({ subsets: ["latin"], weight: ["400", "500", "700"], variable: "--font-roboto", display: "swap" });
const bengali = Noto_Sans_Bengali({ subsets: ["bengali"], variable: "--font-bengali", display: "swap" });

export async function generateMetadata(): Promise<Metadata> {
  const [t, brand] = await Promise.all([getTranslations("meta"), getPublicBrand()]);
  const name = shopName(brand?.businessName); // business name from the public brand details, else the neutral default
  return {
    metadataBase: new URL(siteUrl()),
    title: { default: t("title"), template: `%s | ${name}` },
    description: t("description"),
    applicationName: name,
    manifest: "/manifest.webmanifest",
    openGraph: {
      type: "website",
      siteName: name,
      title: t("title"),
      description: t("description"),
      images: [{ url: "/brand/og-image.png", width: 1200, height: 630, alt: name }],
    },
    robots: { index: true, follow: true },
  };
}

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  viewportFit: "cover", // use the whole screen on phones with notches; safe areas are handled in CSS
  themeColor: [
    { media: "(prefers-color-scheme: light)", color: "#FAF7F3" },
    { media: "(prefers-color-scheme: dark)", color: "#140807" },
  ],
};

export default async function RootLayout({ children }: { children: React.ReactNode }) {
  const [locale, jar] = await Promise.all([getLocale(), cookies()]);
  // Only the small shared set travels with every page; each area's layout adds its own texts (src/messages/scopes.ts).
  const messages = await loadScopedMessages(toLocale(locale), "root");
  const theme = jar.get(COOKIES.theme)?.value;
  const t = await getTranslations("common");

  return (
    <html
      lang={locale}
      data-theme={theme === "light" || theme === "dark" ? theme : undefined}
      className={`${roboto.variable} ${bengali.variable}`}
      suppressHydrationWarning
    >
      <body className="antialiased">
        <a
          href="#main"
          className="fixed start-3 top-3 z-[100] -translate-y-24 rounded-control bg-primary px-4 py-3 font-semibold text-on-primary shadow-pop focus:translate-y-0"
        >
          {t("skipToContent")}
        </a>
        <NextIntlClientProvider locale={locale} messages={messages} timeZone="Asia/Dhaka">
          <Providers>{children}</Providers>
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
