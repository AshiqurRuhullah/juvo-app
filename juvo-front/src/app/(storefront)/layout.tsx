import Link from "next/link";
import { getTranslations } from "next-intl/server";
import { Logo } from "@/components/layout/logo";
import { LanguageSwitch, ThemeToggle } from "@/components/layout/preferences";
import { Button } from "@/components/ui/button";
import { loadSession } from "@/lib/server/session";

/** Public storefront frame: header with logo and account link, footer with contact and social links. */
export default async function StorefrontLayout({ children }: { children: React.ReactNode }) {
  const t = await getTranslations("storefront");
  const session = await loadSession();
  const signedIn = session.state === "ok";
  const accountHref = signedIn ? (session.me.subjectType === "staff" ? "/admin" : "/account") : "/login?as=customer";

  return (
    <div className="flex min-h-dvh flex-col">
      <header className="safe-top sticky top-0 z-20 border-b border-line bg-card/95 backdrop-blur">
        <div className="mx-auto flex h-16 max-w-7xl items-center justify-between gap-3 px-4 sm:px-6">
          <Logo href="/" size={40} priority />
          <nav aria-label={t("mainNav")} className="flex items-center gap-1">
            <LanguageSwitch />
            <ThemeToggle />
            <Button asChild variant={signedIn ? "secondary" : "primary"}>
              <Link href={accountHref}>{signedIn ? t("myAccount") : t("signIn")}</Link>
            </Button>
          </nav>
        </div>
      </header>

      <main id="main" tabIndex={-1} className="flex-1">
        {children}
      </main>

      <footer className="safe-bottom border-t border-line bg-sunken">
        <div className="mx-auto grid max-w-7xl gap-6 px-4 py-8 sm:px-6 md:grid-cols-3">
          <div className="space-y-2">
            <Logo size={36} />
            <p className="max-w-xs text-sm text-muted">{t("tagline")}</p>
          </div>
          <div>
            <h2 className="mb-2 font-sans text-sm font-semibold text-strong">{t("contact")}</h2>
            <ul className="space-y-1 text-sm">
              <li>
                <a className="inline-flex min-h-11 items-center text-muted hover:text-strong hover:underline" href="mailto:juvobd@gmail.com">
                  juvobd@gmail.com
                </a>
              </li>
              <li>
                <a className="inline-flex min-h-11 items-center text-muted hover:text-strong hover:underline" href="https://facebook.com/juvobd" target="_blank" rel="noopener noreferrer">
                  facebook.com/juvobd
                </a>
              </li>
            </ul>
          </div>
          <div>
            <h2 className="mb-2 font-sans text-sm font-semibold text-strong">{t("account")}</h2>
            <ul className="space-y-1 text-sm">
              <li>
                <Link className="inline-flex min-h-11 items-center text-muted hover:text-strong hover:underline" href="/login?as=customer">
                  {t("customerSignIn")}
                </Link>
              </li>
            </ul>
          </div>
        </div>
        <p className="border-t border-line px-4 py-4 text-center text-xs text-muted">© {new Date().getFullYear()} Juvo BD</p>
      </footer>
    </div>
  );
}
