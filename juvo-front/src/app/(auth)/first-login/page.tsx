import type { Metadata } from "next";
import { redirect } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { FirstLoginFlow } from "@/components/auth/first-login-flow";
import { requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("firstLogin");
  return { title: t("title"), robots: { index: false } };
}

/** The limited-session page: choose a permanent password and/or set up two-step verification. */
export default async function FirstLoginPage() {
  const { me } = await requireSession({ subject: "staff", allowLimited: true });
  const staff = me.staff;
  if (!staff || (!staff.mustChangePassword && !staff.requiresTwoFactorSetup)) redirect("/admin");

  return <FirstLoginFlow mustChangePassword={staff.mustChangePassword} requiresTwoFactorSetup={staff.requiresTwoFactorSetup} />;
}
