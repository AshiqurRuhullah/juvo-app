import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import Link from "next/link";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { PageHeader } from "@/components/layout/page-header";
import { Can } from "@/components/providers/session-provider";
import { ImportExportButtons } from "@/modules/catalog/import-export-dialog";
import { ProductsList } from "@/modules/catalog/products-list";
import { requirePermission, requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("catalog.list");
  return { title: t("title") };
}

export default async function ProductsPage({ searchParams }: { searchParams: Promise<{ search?: string }> }) {
  const { me } = await requireSession({ subject: "staff" });
  requirePermission(me, "catalog.product.view", "anywhere");
  const { search } = await searchParams;
  const t = await getTranslations("catalog.list");
  const tn = await getTranslations("nav");

  return (
    <>
      <PageHeader
        title={t("title")}
        description={t("subtitle")}
        crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("groups.catalog") }, { label: tn("items.products") }]}
        actions={
          <>
            <ImportExportButtons />
            <Can permission="catalog.product.create" scope="anywhere">
              <Button asChild>
                <Link href="/admin/products/new">
                  <Plus aria-hidden /> {t("new")}
                </Link>
              </Button>
            </Can>
          </>
        }
      />
      <ProductsList initialSearch={typeof search === "string" ? search.slice(0, 100) : undefined} />
    </>
  );
}
