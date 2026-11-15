import { NextIntlClientProvider } from "next-intl";
import { getLocale } from "next-intl/server";
import { toLocale } from "@/i18n/request";
import { loadScopedMessages, type ScopeName } from "@/messages/scopes";

/**
 * Gives the client components below it the texts of one area only (see src/messages/scopes.ts), instead of the whole catalogue.
 * Used by each area's layout; the root layout provides the small shared set. Server components are not affected (they read the full
 * catalogue on the server).
 */
export async function ScopedIntl({ scope, children }: { scope: ScopeName; children: React.ReactNode }) {
  const locale = toLocale(await getLocale());
  const messages = await loadScopedMessages(locale, scope);
  return (
    <NextIntlClientProvider locale={locale} messages={messages} timeZone="Asia/Dhaka">
      {children}
    </NextIntlClientProvider>
  );
}
