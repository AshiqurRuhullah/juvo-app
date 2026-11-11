import Link from "next/link";
import { getLocale, getTranslations } from "next-intl/server";
import { Logo } from "@/components/layout/logo";
import { loadSession } from "@/lib/server/session";
import { storeGet } from "@/lib/server/store";
import type { StoreCategory } from "@/modules/store/api";
import { StoreHeader } from "@/modules/store/store-header";
import { StoreSessionProvider } from "@/modules/store/store-session";

const footerLink = "inline-flex min-h-11 min-w-11 items-center text-muted hover:text-strong hover:underline";

/** Public storefront frame: header with search, category navigation and cart; footer with contact and social links. */
export default async function StorefrontLayout({ children }: { children: React.ReactNode }) {
  const t = await getTranslations("storefront");
  const s = await getTranslations("store.footer");
  const locale = await getLocale();
  const session = await loadSession();
  const signedIn = session.state === "ok";
  const customer = session.state === "ok" && session.me.subjectType === "customer";
  const accountHref = session.state === "ok" ? (session.me.subjectType === "staff" ? "/admin" : "/account") : "/login?as=customer";
  const categories = (await storeGet<StoreCategory[]>("/categories")) ?? [];

  return (
    <StoreSessionProvider customer={customer}>
      <div className="flex min-h-dvh flex-col">
        <StoreHeader categories={categories} accountHref={accountHref} signedIn={signedIn} />

        <main id="main" tabIndex={-1} className="flex-1">
          {children}
        </main>

        <footer className="safe-bottom border-t border-line bg-sunken">
          <div className="mx-auto grid max-w-7xl gap-6 px-4 py-8 sm:grid-cols-2 sm:px-6 md:grid-cols-4">
            <div className="space-y-2">
              <Logo size={36} />
              <p className="max-w-xs text-sm text-muted">{t("tagline")}</p>
            </div>
            <div>
              <h2 className="mb-2 font-sans text-sm font-semibold text-strong">{s("shop")}</h2>
              <ul className="space-y-1 text-sm">
                <li>
                  <Link className={footerLink} href="/shop">
                    {s("allProducts")}
                  </Link>
                </li>
                {categories.slice(0, 6).map((c) => (
                  <li key={c.id}>
                    <Link className={footerLink} href={`/category/${c.slug}`}>
                      {locale === "bn" && c.nameBn ? c.nameBn : c.nameEn}
                    </Link>
                  </li>
                ))}
              </ul>
            </div>
            <div>
              <h2 className="mb-2 font-sans text-sm font-semibold text-strong">{t("contact")}</h2>
              <ul className="space-y-1 text-sm">
                <li>
                  <a className={footerLink} href="mailto:juvobd@gmail.com">
                    juvobd@gmail.com
                  </a>
                </li>
                <li>
                  <a className={footerLink} href="https://facebook.com/juvobd" target="_blank" rel="noopener noreferrer">
                    facebook.com/juvobd
                  </a>
                </li>
              </ul>
            </div>
            <div>
              <h2 className="mb-2 font-sans text-sm font-semibold text-strong">{t("account")}</h2>
              <ul className="space-y-1 text-sm">
                <li>
                  <Link className={footerLink} href="/login?as=customer">
                    {t("customerSignIn")}
                  </Link>
                </li>
                <li>
                  <Link className={footerLink} href="/cart">
                    {s("cart")}
                  </Link>
                </li>
                <li>
                  <Link className={footerLink} href="/track">
                    {s("trackOrder")}
                  </Link>
                </li>
              </ul>
            </div>
          </div>
          <p className="border-t border-line px-4 py-4 text-center text-xs text-muted">© {new Date().getFullYear()} Juvo BD</p>
        </footer>
      </div>
    </StoreSessionProvider>
  );
}
