"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Archive, Copy, Eye, EyeOff, Rocket, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { Tabs } from "@/components/ui/tabs";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { usePermissions } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import {
  useAllBrands, useCategoryTree, useCreateProduct, useDeleteProduct, useDuplicateProduct, useProduct, useProductAction, useProductStock, useReportError, useSizeCharts,
  useUpdateProduct, type ProductAction,
} from "./api";
import { flattenCategories } from "./category-options";
import { imageSrc, toNumber } from "./format";
import { ProductCard, type ProductCardData } from "./product-card";
import { draftFromProduct, draftToRequest, effectivePrice, emptyDraft, validateDraft, type DraftErrors, type ProductDraft } from "./product-draft";
import { ProductDetailsForm, ProductSeoForm } from "./product-form";
import { ProductImages } from "./product-images";
import { ProductStockGrid } from "./product-stock-grid";
import { ProductVariants } from "./product-variants";

const sameDraft = (a: ProductDraft, b: ProductDraft) => JSON.stringify(a) === JSON.stringify(b);

/** Create a product (no id) or edit one: details, variants and stock, pictures, search texts, with the live storefront card beside it (FR-B08). */
export function ProductEditor({ id, initialTab }: { id?: string; initialTab?: string }) {
  const t = useTranslations("catalog.editor");
  const tn = useTranslations("nav");
  const tf = useTranslations("catalog.form");
  const tc = useTranslations("catalog.common");
  const ts = useTranslations("catalog.status");
  const router = useRouter();
  const report = useReportError();
  const { can } = usePermissions();

  const product = useProduct(id ?? "");
  const tree = useCategoryTree(true);
  const brands = useAllBrands();
  const charts = useSizeCharts();
  const canSeeStock = can("inventory.stock.view", "anywhere");
  const stock = useProductStock(id ?? "", canSeeStock);
  const create = useCreateProduct();
  const update = useUpdateProduct(id ?? "");
  const action = useProductAction(id ?? "");
  const remove = useDeleteProduct();
  const duplicate = useDuplicateProduct();

  const [draft, setDraft] = React.useState<ProductDraft>(emptyDraft);
  const [saved, setSaved] = React.useState<ProductDraft>(emptyDraft);
  const [errors, setErrors] = React.useState<DraftErrors>({});
  const [tab, setTab] = React.useState(id && ["details", "variants", "pictures", "seo"].includes(initialTab ?? "") ? initialTab! : "details");
  const [archiving, setArchiving] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [duplicating, setDuplicating] = React.useState(false);
  const [copyName, setCopyName] = React.useState("");
  const loaded = React.useRef<string | null>(null);

  // Fill the form once per saved version of the product (after a save the server copy is the truth).
  const data = product.data;
  const dataKey = data ? `${data.id}|${data.updatedAt ?? data.createdAt}|${data.status}` : null;
  React.useEffect(() => {
    if (data && dataKey && loaded.current !== dataKey) {
      loaded.current = dataKey;
      const next = draftFromProduct(data);
      setDraft(next);
      setSaved(next);
    }
  }, [data, dataKey]);

  const canSeeCost = id ? !!data && !data.costHidden : can("catalog.product.view-cost", "anywhere");
  const canEdit = id ? can("catalog.product.edit", "anywhere") : can("catalog.product.create", "anywhere");
  const archived = data?.status === "Archived";
  const locked = !canEdit || archived;
  const dirty = !sameDraft(draft, saved);

  const categoryOptions = React.useMemo(() => flattenCategories(tree.data ?? []).map((c) => ({ value: c.id, label: c.label, disabled: !c.isActive && c.id !== draft.categoryId })), [tree.data, draft.categoryId]);
  const brandOptions = React.useMemo(() => (brands.data?.items ?? []).map((b) => ({ value: b.id, label: b.nameEn })), [brands.data]);
  const chartOptions = React.useMemo(() => (charts.data ?? []).map((c) => ({ value: c.id, label: c.nameEn })), [charts.data]);

  const change = React.useCallback(<K extends keyof ProductDraft>(key: K, value: ProductDraft[K]) => {
    setDraft((d) => ({ ...d, [key]: value }));
    setErrors((e) => (e[key] ? { ...e, [key]: undefined } : e));
  }, []);

  // The live card: unsaved form values over the saved variants, pictures and stock.
  const card = React.useMemo<ProductCardData>(() => {
    const images = [...(data?.images ?? [])].sort((a, b) => toNumber(a.sortOrder) - toNumber(b.sortOrder));
    const variants = (data?.variants ?? []).filter((v) => v.isActive);
    const swatches = [...new Map(variants.filter((v) => v.colorName).map((v) => [v.colorName!, { name: v.colorName!, hex: v.colorHex }])).values()];
    const stockByVariant = new Map((stock.data?.variants ?? []).map((v) => [v.variantId, toNumber(v.available)]));
    const known = stock.data !== undefined;
    const sizes = [...new Set(variants.map((v) => v.size).filter((s): s is string => !!s))].map((label) => ({
      label,
      soldOut: known && variants.filter((v) => v.size === label).every((v) => (stockByVariant.get(v.id) ?? 0) <= 0),
    }));
    const lowStock = known && (stock.data?.variants ?? []).some((v) => v.branches.some((b) => b.isLow));
    const soldOut = known && variants.length > 0 && variants.every((v) => (stockByVariant.get(v.id) ?? 0) <= 0);
    return {
      name: draft.nameEn.trim(),
      nameBn: draft.nameBn.trim() || null,
      tagline: draft.shortDescription.trim() || null,
      imageUrl: images[0] ? imageSrc(images[0], "card") : undefined,
      hoverImageUrl: images[1] ? imageSrc(images[1], "card") : undefined,
      blurDataUrl: images[0]?.blurDataUrl,
      regularPrice: toNumber(draft.regularPrice.replace(",", ".")),
      effectivePrice: effectivePrice(draft.regularPrice, draft.salePrice, draft.saleStart, draft.saleEnd),
      isNew: draft.isNew,
      lowStock,
      soldOut,
      swatches,
      sizes,
    };
  }, [draft, data, stock.data]);

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.products"), href: "/admin/products" }, { label: id ? (data?.nameEn ?? "...") : t("new") }];

  if (id && product.isLoading) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-96" />
      </>
    );
  }
  if (id && (product.isError || !data)) {
    const notFound = product.error instanceof ApiError && product.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFoundTitle")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFoundTitle") : t("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/products">{t("back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const save = async () => {
    const found = validateDraft(draft, tf, canSeeCost);
    setErrors(found);
    if (Object.keys(found).length > 0) {
      setTab("details");
      toast.error(t("fixErrors"));
      return;
    }
    try {
      if (!id) {
        const created = await create.mutateAsync(draftToRequest(draft));
        toast.success(t("created"));
        router.push(`/admin/products/${created.id}?tab=variants`);
        return;
      }
      await update.mutateAsync(draftToRequest(draft, data));
      toast.success(t("saved"));
    } catch (error) {
      const fields = report(error);
      setErrors(Object.fromEntries(Object.entries(fields).map(([k, v]) => [k, v])) as DraftErrors);
      setTab("details");
    }
  };

  const run = async (kind: ProductAction) => {
    try {
      await action.mutateAsync(kind);
      toast.success(t(`done.${kind}`));
    } catch (error) {
      report(error);
    } finally {
      setArchiving(false);
    }
  };

  const problems = data?.publishProblems ?? [];
  const busy = create.isPending || update.isPending;

  const tabs = id
    ? [
        { id: "details", label: t("tabs.details") },
        { id: "variants", label: t("tabs.variants") },
        { id: "pictures", label: t("tabs.pictures") },
        { id: "seo", label: t("tabs.seo") },
      ]
    : [{ id: "details", label: t("tabs.details") }];

  return (
    <>
      <PageHeader
        title={id ? (data?.nameEn ?? "") : t("newTitle")}
        description={id ? undefined : t("newDescription")}
        crumbs={crumbs}
        actions={
          id && data ? (
            <>
              {can("catalog.product.duplicate", "anywhere") ? (
                <Button variant="secondary" onClick={() => { setCopyName(`${data.nameEn} (copy)`); setDuplicating(true); }}>
                  <Copy aria-hidden /> {t("duplicate")}
                </Button>
              ) : null}
              {data.status === "Draft" && can("catalog.product.publish", "anywhere") ? (
                <Button onClick={() => void run("publish")} loading={action.isPending} disabled={problems.length > 0 || dirty}>
                  <Rocket aria-hidden /> {t("publish")}
                </Button>
              ) : null}
              {data.status === "Published" && can("catalog.product.publish", "anywhere") ? (
                <Button variant="secondary" onClick={() => void run("unpublish")} loading={action.isPending}>
                  <EyeOff aria-hidden /> {t("unpublish")}
                </Button>
              ) : null}
              {!archived && can("catalog.product.archive", "anywhere") ? (
                <Button variant="secondary" onClick={() => setArchiving(true)}>
                  <Archive aria-hidden /> {t("archive")}
                </Button>
              ) : null}
              {data.status === "Draft" && can("catalog.product.archive", "anywhere") ? (
                <Button variant="ghost" onClick={() => setDeleting(true)}>
                  <Trash2 aria-hidden /> {t("delete")}
                </Button>
              ) : null}
            </>
          ) : undefined
        }
      />

      {archived ? <Alert variant="warning" className="mb-4">{t("archivedNotice")}</Alert> : null}
      {id && !canEdit && !archived ? <Alert variant="info" className="mb-4">{t("readOnly")}</Alert> : null}

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_20rem] xl:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="min-w-0 space-y-5">
          <Tabs tabs={tabs} value={tab} onValueChange={setTab} label={t("tabsLabel")}>
            {(active) => {
              if (active === "variants" && data) {
                return (
                  <div className="space-y-5">
                    <ProductVariants product={data} stock={stock.data} disabled={archived} />
                    {stock.data ? <ProductStockGrid product={data} stock={stock.data} /> : null}
                  </div>
                );
              }
              if (active === "pictures" && data) return <ProductImages product={data} disabled={archived} />;
              if (active === "seo") return <ProductSeoForm draft={draft} errors={errors} onChange={change} disabled={locked} />;
              return <ProductDetailsForm draft={draft} errors={errors} onChange={change} disabled={locked} options={{ categories: categoryOptions, brands: brandOptions, sizeCharts: chartOptions }} canSeeCost={canSeeCost} />;
            }}
          </Tabs>

          {(tabNeedsSave(tab)) && canEdit && !archived ? (
            <div className="sticky bottom-0 z-10 -mx-4 border-t border-line bg-surface/95 px-4 py-3 backdrop-blur safe-bottom sm:mx-0 sm:rounded-card sm:border sm:px-5">
              <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                <p aria-live="polite" className="text-sm text-muted">
                  {dirty || !id ? t("unsaved") : t("allSaved")}
                </p>
                <div className="flex gap-2">
                  {id ? (
                    <Button variant="secondary" onClick={() => { setDraft(saved); setErrors({}); }} disabled={!dirty || busy}>
                      {t("discard")}
                    </Button>
                  ) : (
                    <Button asChild variant="secondary">
                      <Link href="/admin/products">{tc("cancel")}</Link>
                    </Button>
                  )}
                  <Button onClick={() => void save()} loading={busy} disabled={id ? !dirty : false}>
                    {id ? tc("save") : t("createAction")}
                  </Button>
                </div>
              </div>
            </div>
          ) : null}
        </div>

        <aside className="space-y-4 lg:sticky lg:top-24 lg:self-start" aria-label={t("previewTitle")}>
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-lg">
                <Eye className="size-5 text-muted" aria-hidden /> {t("previewTitle")}
              </CardTitle>
              <CardDescription>{t("previewHint")}</CardDescription>
            </CardHeader>
            <CardContent className="flex justify-center">
              <ProductCard data={card} preview />
            </CardContent>
          </Card>

          {id && data ? (
            <Card>
              <CardHeader>
                <CardTitle className="flex flex-wrap items-center gap-2 text-lg">
                  {t("statusTitle")}
                  <Badge variant={data.status === "Published" ? "success" : data.status === "Archived" ? "outline" : "neutral"}>{ts(data.status)}</Badge>
                </CardTitle>
              </CardHeader>
              <CardContent className="space-y-2">
                {data.status === "Draft" ? (
                  problems.length > 0 ? (
                    <>
                      <p className="text-sm font-semibold text-strong">{t("beforePublish")}</p>
                      <ul className="list-disc space-y-1 ps-5 text-sm text-muted">
                        {problems.map((p) => (
                          <li key={p}>{p}</li>
                        ))}
                      </ul>
                    </>
                  ) : (
                    <p className="text-sm text-success">{t("readyToPublish")}</p>
                  )
                ) : null}
                {data.status === "Published" ? <p className="text-sm text-muted">{t("liveNotice")}</p> : null}
              </CardContent>
            </Card>
          ) : null}
        </aside>
      </div>

      <ConfirmDialog
        open={archiving}
        onOpenChange={setArchiving}
        title={t("archiveTitle")}
        description={t("archiveDescription")}
        confirmLabel={t("archive")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={action.isPending}
        onConfirm={() => run("archive")}
      />
      <ConfirmDialog
        open={deleting}
        onOpenChange={setDeleting}
        title={t("deleteTitle")}
        description={t("deleteDescription")}
        confirmLabel={t("delete")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={async () => {
          if (!id) return;
          try {
            await remove.mutateAsync(id);
            toast.success(t("deleted"));
            router.push("/admin/products");
          } catch (error) {
            report(error);
            setDeleting(false);
          }
        }}
      />
      <Dialog open={duplicating} onOpenChange={(open) => (duplicate.isPending ? undefined : setDuplicating(open))}>
        <ModalContent size="sm" closeLabel={tc("close")}>
          <DialogTitle className="font-display text-xl font-semibold text-strong">{t("duplicateTitle")}</DialogTitle>
          <DialogDescription className="mt-1 text-muted">{t("duplicateDescription")}</DialogDescription>
          <div className="mt-4">
            <Field label={t("duplicateName")} htmlFor="copy-name">
              <Input id="copy-name" value={copyName} onChange={(e) => setCopyName(e.target.value)} maxLength={200} autoComplete="off" />
            </Field>
          </div>
          <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <Button variant="secondary" onClick={() => setDuplicating(false)} disabled={duplicate.isPending}>
              {tc("cancel")}
            </Button>
            <Button
              loading={duplicate.isPending}
              onClick={async () => {
                if (!id) return;
                try {
                  const copy = await duplicate.mutateAsync({ id, nameEn: copyName.trim() || null });
                  toast.success(t("duplicated"));
                  setDuplicating(false);
                  router.push(`/admin/products/${copy.id}`);
                } catch (error) {
                  report(error);
                }
              }}
            >
              {t("duplicate")}
            </Button>
          </div>
        </ModalContent>
      </Dialog>
    </>
  );
}

/** The save bar belongs to the form tabs; variants and pictures save themselves as they change. */
function tabNeedsSave(tab: string) {
  return tab === "details" || tab === "seo";
}
