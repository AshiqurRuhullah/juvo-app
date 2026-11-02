import { getTranslations } from "next-intl/server";
import Link from "next/link";
import { Logo } from "@/components/layout/logo";
import { LanguageSwitch, ThemeToggle } from "@/components/layout/preferences";
import { Card, CardContent } from "@/components/ui/surfaces";

/** Centered, branded frame for sign-in and password pages. */
export default async function AuthLayout({ children }: { children: React.ReactNode }) {
  const t = await getTranslations("common");
  return (
    <div className="flex min-h-dvh flex-col">
      <header className="safe-top flex items-center justify-between gap-2 px-4 py-3 sm:px-6">
        <Logo href="/" size={44} priority />
        <div className="flex items-center gap-1">
          <LanguageSwitch />
          <ThemeToggle />
        </div>
      </header>
      <main id="main" className="flex flex-1 items-start justify-center px-4 pb-10 pt-4 sm:items-center sm:px-6">
        <div className="w-full max-w-md">
          <Card className="shadow-pop">
            <CardContent className="p-5 pt-5 sm:p-8 sm:pt-8">{children}</CardContent>
          </Card>
        </div>
      </main>
      <footer className="safe-bottom px-4 py-5 text-center text-sm text-muted">
        <Link href="/" className="inline-flex min-h-11 items-center rounded-control px-2 hover:underline">
          {t("backToStore")}
        </Link>
      </footer>
    </div>
  );
}
