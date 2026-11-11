/**
 * Every message of one language. The base file (`en.json`, `bn.json`) holds the shell, auth and UMS texts; each later module keeps its
 * own file next to it (`catalog.en.json`, `inventory.en.json` ...) so modules can be built side by side without editing one huge file.
 * Each fragment becomes one top-level namespace with the fragment's name.
 */
export const MESSAGE_FRAGMENTS = ["catalog", "lookups", "inventory", "otp", "dashboard2", "store", "cart", "myaccount", "orders", "payments", "billing", "notifications"] as const;

export type MessageLocale = "en" | "bn";

export async function loadMessages(locale: MessageLocale): Promise<Record<string, unknown>> {
  const base = ((await import(`./${locale}.json`)) as { default: Record<string, unknown> }).default;
  const extra = await Promise.all(
    MESSAGE_FRAGMENTS.map(async (name) => [name, ((await import(`./${name}.${locale}.json`)) as { default: unknown }).default] as const),
  );
  return { ...base, ...Object.fromEntries(extra) };
}
