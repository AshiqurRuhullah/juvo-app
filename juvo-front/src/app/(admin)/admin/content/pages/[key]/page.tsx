import type { Metadata } from "next";
import { notFound } from "next/navigation";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { requirePermission, requireSession } from "@/lib/server/session";
import { isPageKey } from "@/modules/content/logic";
import { PageEditor } from "@/modules/content/page-editor";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("content.editor");
  return { title: t("pageTitle") };
}

export default async function Page({ params }: { params: Promise<{ key: string }> }) {
  const { key } = await params;
  if (!isPageKey(key)) notFound();
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "content.page.view", "anywhere");
  const t = await getTranslations("content.pages");
  const tn = await getTranslations("content.pageNames");
  const tn2 = await getTranslations("nav");
  return (
    <>
      <PageHeader
        title={tn(key)}
        crumbs={[{ label: tn2("items.dashboard"), href: "/admin" }, { label: t("title"), href: "/admin/content/pages" }, { label: tn(key) }]}
      />
      <PageEditor pageKey={key} />
    </>
  );
}
