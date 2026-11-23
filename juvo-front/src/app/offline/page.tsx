import type { Metadata } from "next";
import Link from "next/link";
import { RetryButton } from "@/modules/pwa/retry-button";
import en from "@/messages/pwa.en.json";
import bn from "@/messages/pwa.bn.json";

export const metadata: Metadata = { title: "Offline", robots: { index: false, follow: false } };

/**
 * Shown by the service worker when a shop page cannot be loaded without a connection. It is stored by the worker once, so it must
 * not depend on the visitor: no session, no cart, both languages at once (English and Bengali).
 */
export default function OfflinePage() {
  return (
    <main id="main" tabIndex={-1} className="mx-auto flex min-h-dvh max-w-xl flex-col items-center justify-center gap-6 px-4 py-10 text-center">
      {/* A plain image: the worker stores /brand files, not optimised /_next/image URLs. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img src="/brand/logo-badge-256.png" alt="Juvo BD" width={56} height={56} className="rounded-full" />
      {[
        { lang: "en", m: en.offline },
        { lang: "bn", m: bn.offline },
      ].map(({ lang, m }) => (
        <section key={lang} lang={lang} className="space-y-2">
          <h1 className="font-display text-2xl font-semibold text-strong">{m.title}</h1>
          <p className="text-muted">{m.body}</p>
        </section>
      ))}
      <RetryButton label={`${en.offline.retry} / ${bn.offline.retry}`} />
      <nav className="flex flex-wrap items-center justify-center gap-2" aria-label="Juvo BD">
        <Link href="/" className="inline-flex min-h-11 items-center rounded-control px-4 font-medium text-primary underline-offset-4 hover:underline">
          {en.offline.home} / {bn.offline.home}
        </Link>
        <Link href="/shop" className="inline-flex min-h-11 items-center rounded-control px-4 font-medium text-primary underline-offset-4 hover:underline">
          {en.offline.shop} / {bn.offline.shop}
        </Link>
      </nav>
    </main>
  );
}
