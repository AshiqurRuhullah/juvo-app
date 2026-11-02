import { getRequestConfig } from "next-intl/server";
import { cookies } from "next/headers";
import { COOKIES } from "@/lib/auth/cookies";

export const LOCALES = ["en", "bn"] as const;
export type Locale = (typeof LOCALES)[number];

export function toLocale(value: string | undefined | null): Locale {
  return value === "bn" ? "bn" : "en";
}

/**
 * English and Bengali without locale prefixes in the URL: the language is a preference cookie, so the admin, account and
 * checkout URLs stay the same in both languages. (Storefront pages that need per-language URLs for search engines are revisited
 * in Step 7.) English is the default.
 */
export default getRequestConfig(async () => {
  const store = await cookies();
  const locale = toLocale(store.get(COOKIES.locale)?.value);
  return {
    locale,
    messages: (await import(`../messages/${locale}.json`)).default,
    timeZone: "Asia/Dhaka",
  };
});
