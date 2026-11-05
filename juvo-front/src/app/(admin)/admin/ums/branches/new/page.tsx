import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { BranchEditor } from "@/modules/ums/branch-editor";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("ums.branches");
  return { title: t("newTitle") };
}

export default async function NewBranchPage() {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "ums.branch.create", "anywhere");
  return <BranchEditor />;
}
