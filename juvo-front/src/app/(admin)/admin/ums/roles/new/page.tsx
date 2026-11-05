import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { RoleEditor } from "@/modules/ums/role-editor";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("ums.roles");
  return { title: t("newTitle") };
}

export default async function NewRolePage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "ums.role.create", "anywhere");
  return <RoleEditor />;
}
