import type { Metadata } from "next";
import { cookies } from "next/headers";
import { AppShell } from "@/components/layout/app-shell";
import { SessionProvider } from "@/components/providers/session-provider";
import { COOKIES } from "@/lib/auth/cookies";
import { requireSession } from "@/lib/server/session";

export const metadata: Metadata = { title: { default: "My account", template: "%s | Juvo BD" }, robots: { index: false, follow: false } };

/** The customer account: sidebar on tablets and desktops, drawer and bottom bar on phones (same shell as the admin). */
export default async function AccountLayout({ children }: { children: React.ReactNode }) {
  const { me } = await requireSession({ subject: "customer" });
  const jar = await cookies();

  return (
    <SessionProvider me={me}>
      <AppShell area="account" initialCollapsed={jar.get(COOKIES.sidebar)?.value === "collapsed"}>
        {children}
      </AppShell>
    </SessionProvider>
  );
}
