import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { LoginCard } from "@/components/auth/login-card";
import { loadSession } from "@/lib/server/session";
import { safeNext } from "@/lib/utils";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("auth");
  return { title: t("title"), robots: { index: false } };
}

export default async function LoginPage({ searchParams }: { searchParams: Promise<{ next?: string; reason?: string; as?: string }> }) {
  const { next, reason, as } = await searchParams;

  // Already signed in: go where they were headed (or to their own area).
  const session = await loadSession();
  if (session.state === "ok") {
    const me = session.me;
    if (me.subjectType === "staff") {
      const limited = me.staff?.mustChangePassword || me.staff?.requiresTwoFactorSetup;
      redirect(limited ? "/first-login" : safeNext(next, "/admin"));
    }
    redirect(safeNext(next, "/account"));
  }

  return <LoginCard next={next} reason={reason} initial={as === "customer" ? "customer" : "staff"} />;
}
