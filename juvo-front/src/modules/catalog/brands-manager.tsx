"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can } from "@/components/providers/session-provider";
import { SearchBox, useListState, useTableLabels } from "@/components/data/list-kit";
import { useBrands, useCreateBrand, useDeleteBrand, useReportError, useUpdateBrand, type Brand } from "./api";
import { toNumber } from "./format";

type DialogState = { kind: "new" } | { kind: "edit"; brand: Brand } | null;

/** Brands: a searchable list with a create / edit dialog and a delete that the API refuses while products use the brand. */
export function BrandsManager() {
  const t = useTranslations("lookups.brands");
  const tc = useTranslations("lookups.common");
  const labels = useTableLabels();
  const report = useReportError();
  const state = useListState<Record<string, never>>({}, 20, [{ id: "name", desc: false }]);
  const query = useBrands({ page: state.page, pageSize: state.pageSize, sort: state.sort, search: state.search });
  const remove = useDeleteBrand();
  const [dialog, setDialog] = React.useState<DialogState>(null);
  const [deleting, setDeleting] = React.useState<Brand | null>(null);

  const columns = React.useMemo<ColumnDef<Brand, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="inline-flex min-h-11 flex-col justify-center">
            <span className="font-semibold text-strong [overflow-wrap:anywhere]">{row.original.nameEn}</span>
            {row.original.nameBn ? <span className="text-sm text-muted [overflow-wrap:anywhere]">{row.original.nameBn}</span> : null}
          </span>
        ),
      },
      { id: "slug", header: t("columns.slug"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="text-muted [overflow-wrap:anywhere]">{row.original.slug}</span> },
      { id: "products", header: t("columns.products"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => tc("products", { count: toNumber(row.original.productCount) }) },
      {
        id: "status",
        header: t("columns.status"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <Badge variant={row.original.isActive ? "success" : "outline"}>{row.original.isActive ? tc("active") : tc("inactive")}</Badge>,
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => (
          <span className="flex justify-end gap-1">
            <Can permission="catalog.brand.edit" scope="anywhere">
              <Button variant="ghost" size="icon" aria-label={t("edit", { name: row.original.nameEn })} onClick={() => setDialog({ kind: "edit", brand: row.original })}>
                <Pencil aria-hidden />
              </Button>
            </Can>
            <Can permission="catalog.brand.delete" scope="anywhere">
              <Button variant="ghost" size="icon" aria-label={t("delete", { name: row.original.nameEn })} onClick={() => setDeleting(row.original)}>
                <Trash2 aria-hidden />
              </Button>
            </Can>
          </span>
        ),
      },
    ],
    [t, tc],
  );

  const confirmDelete = async () => {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast.success(tc("deleted"));
    } catch (error) {
      report(error);
    }
    setDeleting(null);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
        <SearchBox className="sm:w-80" value={state.searchText} onChange={state.setSearchText} placeholder={t("searchPlaceholder")} label={tc("search")} />
        <Can permission="catalog.brand.create" scope="anywhere">
          <Button onClick={() => setDialog({ kind: "new" })}>
            <Plus aria-hidden /> {t("add")}
          </Button>
        </Can>
      </div>
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable
        caption={t("columns.name")}
        columns={columns}
        data={query.data?.items ?? []}
        loading={query.isLoading}
        getRowId={(b) => b.id}
        labels={labels}
        page={state.page}
        pageSize={state.pageSize}
        total={toNumber(query.data?.total)}
        onPageChange={state.setPage}
        sorting={state.sorting}
        onSortingChange={state.setSorting}
      />

      <Dialog open={dialog !== null} onOpenChange={(open) => !open && setDialog(null)}>
        <ModalContent size="md" closeLabel={tc("close")}>
          {dialog ? <BrandForm key={dialog.kind === "edit" ? dialog.brand.id : "new"} brand={dialog.kind === "edit" ? dialog.brand : undefined} onDone={() => setDialog(null)} /> : null}
        </ModalContent>
      </Dialog>

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("deleteTitle")}
        description={t("deleteDescription", { name: deleting?.nameEn ?? "" })}
        confirmLabel={t("deleteConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={confirmDelete}
      />
    </div>
  );
}

function BrandForm({ brand, onDone }: { brand?: Brand; onDone: () => void }) {
  const t = useTranslations("lookups.brands");
  const tc = useTranslations("lookups.common");
  const report = useReportError();
  const create = useCreateBrand();
  const update = useUpdateBrand();
  const [nameEn, setNameEn] = React.useState(brand?.nameEn ?? "");
  const [nameBn, setNameBn] = React.useState(brand?.nameBn ?? "");
  const [slug, setSlug] = React.useState(brand?.slug ?? "");
  const [isActive, setIsActive] = React.useState(brand?.isActive ?? true);
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!nameEn.trim()) {
      setErrors({ nameEn: tc("nameRequired") });
      return;
    }
    const body = { nameEn: nameEn.trim(), nameBn: nameBn.trim() || null, slug: slug.trim() || null, isActive };
    try {
      if (brand) await update.mutateAsync({ id: brand.id, body });
      else await create.mutateAsync(body);
      toast.success(brand ? tc("saved") : tc("created"));
      onDone();
    } catch (error) {
      setErrors(report(error));
    }
  };

  return (
    <form onSubmit={submit} noValidate className="space-y-5">
      <div>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{brand ? t("dialogEditTitle") : t("dialogNewTitle")}</DialogTitle>
        <DialogDescription className="sr-only">{brand ? t("dialogEditTitle") : t("dialogNewTitle")}</DialogDescription>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tc("nameEn")} htmlFor="brand-name-en" error={errors.nameEn} required>
          <Input id="brand-name-en" value={nameEn} onChange={(e) => setNameEn(e.target.value)} autoComplete="off" aria-invalid={!!errors.nameEn} />
        </Field>
        <Field label={tc("nameBn")} htmlFor="brand-name-bn" error={errors.nameBn}>
          <Input id="brand-name-bn" value={nameBn} onChange={(e) => setNameBn(e.target.value)} autoComplete="off" lang="bn" />
        </Field>
        <Field label={tc("slug")} htmlFor="brand-slug" hint={tc("slugHint")} error={errors.slug} className="sm:col-span-2">
          <Input id="brand-slug" value={slug} onChange={(e) => setSlug(e.target.value)} autoCapitalize="none" autoComplete="off" />
        </Field>
      </div>
      <ChoiceRow htmlFor="brand-active">
        <Switch id="brand-active" checked={isActive} onCheckedChange={setIsActive} />
        <span>
          <span className="block font-medium">{tc("isActive")}</span>
          <span className="block text-sm text-muted">{tc("isActiveHint")}</span>
        </span>
      </ChoiceRow>
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="secondary" onClick={onDone}>
          {tc("cancel")}
        </Button>
        <Button type="submit" loading={create.isPending || update.isPending}>
          {tc("save")}
        </Button>
      </div>
    </form>
  );
}
