import type { Metadata } from "next";
import { cookies } from "next/headers";
import { AppShell } from "@/components/layout/app-shell";
import { SessionProvider } from "@/components/providers/session-provider";
import { COOKIES } from "@/lib/auth/cookies";
import { requireSession } from "@/lib/server/session";

export const metadata: Metadata = { title: { default: "Admin", template: "%s | Juvo BD Admin" }, robots: { index: false, follow: false } };

/**
 * The admin area. Signed-in staff only: the proxy renews an expired session, this layout loads /auth/me (roles, branches and
 * permissions), sends limited sessions to the first-login flow, and sends customers to the 403 page.
 */
export default async function AdminLayout({ children }: { children: React.ReactNode }) {
  const { me } = await requireSession({ subject: "staff" });
  const jar = await cookies();
  const collapsed = jar.get(COOKIES.sidebar)?.value === "collapsed";

  return (
    <SessionProvider me={me}>
      <AppShell area="admin" initialCollapsed={collapsed}>
        {children}
      </AppShell>
    </SessionProvider>
  );
}
