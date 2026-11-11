import type { Metadata, Viewport } from "next";
import { Noto_Sans_Bengali, Roboto } from "next/font/google";
import { cookies } from "next/headers";
import { NextIntlClientProvider } from "next-intl";
import { getLocale, getMessages, getTranslations } from "next-intl/server";
import { Providers } from "@/components/providers/providers";
import { COOKIES } from "@/lib/auth/cookies";
import "./globals.css";

// Self-hosted by next/font at build time (no request to Google when a visitor opens the site). Roboto is Apache-2.0, Noto Sans Bengali is SIL OFL.
const roboto = Roboto({ subsets: ["latin"], weight: ["400", "500", "700"], variable: "--font-roboto", display: "swap" });
const bengali = Noto_Sans_Bengali({ subsets: ["bengali"], variable: "--font-bengali", display: "swap" });

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("meta");
  return {
    metadataBase: new URL(process.env.NEXT_PUBLIC_SITE_URL ?? "http://localhost:3000"),
    title: { default: t("title"), template: "%s | Juvo BD" },
    description: t("description"),
    applicationName: "Juvo BD",
    manifest: "/manifest.webmanifest",
    openGraph: {
      type: "website",
      siteName: "Juvo BD",
      title: t("title"),
      description: t("description"),
      images: [{ url: "/brand/og-image.png", width: 1200, height: 630, alt: "Juvo BD" }],
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
  const [locale, messages, jar] = await Promise.all([getLocale(), getMessages(), cookies()]);
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
        <NextIntlClientProvider locale={locale} messages={messages}>
          <Providers>{children}</Providers>
        </NextIntlClientProvider>
      </body>
    </html>
  );
}
