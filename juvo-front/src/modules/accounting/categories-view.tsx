"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Plus } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, ChoiceRow, Field, Input } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useTableLabels } from "@/components/data/list-kit";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useExpenseCategories, useSaveExpenseCategory, type ExpenseCategory } from "./api";
import { useAccountingPermissions } from "./permissions";

/** The categories tab: every category with how many expenses use it; create, rename, move under another one, switch off. */
export function CategoriesView() {
  const t = useTranslations("accounting.categories");
  const tc = useTranslations("accounting.common");
  const labels = useTableLabels();
  const perms = useAccountingPermissions();
  const query = useExpenseCategories(true);
  const [editing, setEditing] = React.useState<ExpenseCategory | null>(null);
  const [open, setOpen] = React.useState(false);
  const all = React.useMemo(() => query.data ?? [], [query.data]);
  const names = React.useMemo(() => new Map(all.map((c) => [c.id, c.nameEn])), [all]);

  const edit = React.useCallback((c: ExpenseCategory | null) => {
    setEditing(c);
    setOpen(true);
  }, []);

  const columns = React.useMemo<ColumnDef<ExpenseCategory, unknown>[]>(
    () => [
      { id: "name", header: t("columns.name"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="font-semibold text-strong [overflow-wrap:anywhere]">{row.original.nameEn}</span> },
      { id: "bn", header: t("columns.nameBn"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span lang="bn">{row.original.nameBn ?? "-"}</span> },
      { id: "parent", header: t("columns.parent"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span>{row.original.parentId ? (names.get(row.original.parentId) ?? "-") : "-"}</span> },
      { id: "used", header: t("columns.used"), enableSorting: false, meta: { numeric: true, secondary: true }, cell: ({ row }) => <span className="tabular-nums">{toNumber(row.original.expenseCount)}</span> },
      { id: "status", header: t("columns.status"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant={row.original.isActive ? "success" : "outline"}>{row.original.isActive ? t("active") : t("inactive")}</Badge> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) =>
          perms.expenseEdit ? (
            <Button variant="secondary" onClick={() => edit(row.original)} aria-label={t("editFor", { name: row.original.nameEn })}>
              {t("edit")}
            </Button>
          ) : null,
      },
    ],
    [t, names, perms.expenseEdit, edit],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-sm text-muted">{t("hint")}</p>
        {perms.expenseEdit ? (
          <Button onClick={() => edit(null)} data-testid="new-category">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={all} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} />
      {open ? <CategoryDialog open={open} onOpenChange={setOpen} category={editing} parents={all.filter((c) => !c.parentId && c.id !== editing?.id)} /> : null}
    </div>
  );
}

function CategoryDialog({ open, onOpenChange, category, parents }: { open: boolean; onOpenChange: (open: boolean) => void; category: ExpenseCategory | null; parents: ExpenseCategory[] }) {
  const t = useTranslations("accounting.categories.form");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="sm" closeLabel={t("close")}>
        <Body category={category} parents={parents} close={() => onOpenChange(false)} />
      </ModalContent>
    </Dialog>
  );
}

function Body({ category, parents, close }: { category: ExpenseCategory | null; parents: ExpenseCategory[]; close: () => void }) {
  const t = useTranslations("accounting.categories.form");
  const tc = useTranslations("accounting.common");
  const report = useReportError();
  const save = useSaveExpenseCategory();
  const [nameEn, setNameEn] = React.useState(category?.nameEn ?? "");
  const [nameBn, setNameBn] = React.useState(category?.nameBn ?? "");
  const [parentId, setParentId] = React.useState(category?.parentId ?? "");
  const [active, setActive] = React.useState(category?.isActive ?? true);
  const [error, setError] = React.useState<string | null>(null);
  const used = toNumber(category?.expenseCount);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!nameEn.trim()) {
      setError(t("nameRequired"));
      return;
    }
    setError(null);
    try {
      await save.mutateAsync({ id: category?.id, body: { nameEn: nameEn.trim(), nameBn: nameBn.trim() || null, parentId: parentId || null, isActive: active } });
      toast.success(category ? t("saved") : t("created"));
      close();
    } catch (e) {
      report(e);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{category ? t("editTitle") : t("newTitle")}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{t("description")}</DialogDescription>
      <div className="mt-4 space-y-4">
        <Field label={t("nameEn")} htmlFor="cat-en" required error={error ?? undefined}>
          <Input id="cat-en" value={nameEn} maxLength={120} onChange={(e) => setNameEn(e.target.value)} aria-invalid={!!error} autoComplete="off" />
        </Field>
        <Field label={t("nameBn")} htmlFor="cat-bn">
          <Input id="cat-bn" lang="bn" value={nameBn} maxLength={120} onChange={(e) => setNameBn(e.target.value)} autoComplete="off" />
        </Field>
        <Field label={t("parent")} htmlFor="cat-parent" hint={t("parentHint")}>
          <Select id="cat-parent" value={parentId || "__none"} onValueChange={(v) => setParentId(v === "__none" ? "" : v)} options={[{ value: "__none", label: t("noParent") }, ...parents.map((p) => ({ value: p.id, label: p.nameEn }))]} placeholder={t("parent")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        {category ? (
          <>
            <ChoiceRow htmlFor="cat-active">
              <Checkbox id="cat-active" checked={active} onCheckedChange={(c) => setActive(c === true)} />
              {t("active")}
            </ChoiceRow>
            {used > 0 ? <Alert variant="info">{t("inUse", { count: used })}</Alert> : null}
          </>
        ) : null}
      </div>
      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="category-save">
          {tc("save")}
        </Button>
      </div>
    </form>
  );
}
