"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ArrowDown, ArrowUp, Layers, Pencil, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can } from "@/components/providers/session-provider";
import { useCategoryTree, useCreateCategory, useDeleteCategory, useReorderCategories, useReportError, useUpdateCategory, type CategoryNode } from "./api";
import { toNumber } from "./format";

type Flat = { node: CategoryNode; parentId: string | null; depth: number; path: string; siblings: string[] };

/** The tree as a flat list in display order, with the parent, depth, a readable path and the ordered ids of the siblings. */
function flatten(nodes: CategoryNode[], parentId: string | null = null, depth = 0, prefix = ""): Flat[] {
  const siblings = nodes.map((n) => n.id);
  return nodes.flatMap((node) => {
    const path = prefix ? `${prefix} › ${node.nameEn}` : node.nameEn;
    return [{ node, parentId, depth, path, siblings }, ...flatten(node.children, node.id, depth + 1, path)];
  });
}

function descendantIds(node: CategoryNode): string[] {
  return node.children.flatMap((c) => [c.id, ...descendantIds(c)]);
}

type DialogState = { kind: "new"; parentId: string | null } | { kind: "edit"; id: string } | null;

/** The category tree (FR-B11): add, add a sub-category, edit, move among siblings, delete an empty one. */
export function CategoriesManager() {
  const t = useTranslations("lookups.categories");
  const tc = useTranslations("lookups.common");
  const report = useReportError();
  const tree = useCategoryTree(true);
  const reorder = useReorderCategories();
  const remove = useDeleteCategory();
  const [dialog, setDialog] = React.useState<DialogState>(null);
  const [deleting, setDeleting] = React.useState<{ id: string; name: string } | null>(null);

  const flat = React.useMemo(() => flatten(tree.data ?? []), [tree.data]);

  const move = async (entry: Flat, direction: -1 | 1) => {
    const index = entry.siblings.indexOf(entry.node.id);
    const target = index + direction;
    if (target < 0 || target >= entry.siblings.length) return;
    const ids = [...entry.siblings];
    [ids[index], ids[target]] = [ids[target]!, ids[index]!];
    try {
      await reorder.mutateAsync(ids);
    } catch (error) {
      report(error);
    }
  };

  const confirmDelete = async () => {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast.success(tc("deleted"));
      setDeleting(null);
    } catch (error) {
      report(error);
      setDeleting(null);
    }
  };

  const renderLevel = (nodes: CategoryNode[], parent: Flat | null): React.ReactNode => (
    <ul className={parent ? "ms-4 border-s border-line ps-3 sm:ms-6 sm:ps-4" : "space-y-1"} aria-label={parent ? undefined : t("treeLabel")}>
      {nodes.map((node, index) => {
        const entry = flat.find((f) => f.node.id === node.id)!;
        return (
          <li key={node.id} className="space-y-1">
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 rounded-control px-2 py-1 hover:bg-sunken/60">
              <div className="min-w-0 flex-1 basis-48">
                <p className="font-semibold text-strong [overflow-wrap:anywhere]">
                  {node.nameEn}
                  {node.nameBn ? <span className="ms-2 font-normal text-muted">{node.nameBn}</span> : null}
                </p>
                <p className="flex flex-wrap items-center gap-2 text-sm text-muted">
                  <span>{tc("products", { count: toNumber(node.productCount) })}</span>
                  <Badge variant={node.isActive ? "success" : "outline"}>{node.isActive ? tc("active") : tc("inactive")}</Badge>
                </p>
              </div>
              <Can permission="catalog.category.edit" scope="anywhere">
                <Button variant="ghost" size="icon" aria-label={t("moveUp", { name: node.nameEn })} disabled={index === 0 || reorder.isPending} onClick={() => void move(entry, -1)}>
                  <ArrowUp aria-hidden />
                </Button>
                <Button variant="ghost" size="icon" aria-label={t("moveDown", { name: node.nameEn })} disabled={index === nodes.length - 1 || reorder.isPending} onClick={() => void move(entry, 1)}>
                  <ArrowDown aria-hidden />
                </Button>
              </Can>
              <Can permission="catalog.category.create" scope="anywhere">
                <Button variant="ghost" size="icon" aria-label={t("addChild", { name: node.nameEn })} onClick={() => setDialog({ kind: "new", parentId: node.id })}>
                  <Plus aria-hidden />
                </Button>
              </Can>
              <Can permission="catalog.category.edit" scope="anywhere">
                <Button variant="ghost" size="icon" aria-label={t("edit", { name: node.nameEn })} onClick={() => setDialog({ kind: "edit", id: node.id })}>
                  <Pencil aria-hidden />
                </Button>
              </Can>
              <Can permission="catalog.category.delete" scope="anywhere">
                <Button variant="ghost" size="icon" aria-label={t("delete", { name: node.nameEn })} onClick={() => setDeleting({ id: node.id, name: node.nameEn })}>
                  <Trash2 aria-hidden />
                </Button>
              </Can>
            </div>
            {node.children.length > 0 ? renderLevel(node.children, entry) : null}
          </li>
        );
      })}
    </ul>
  );

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Can permission="catalog.category.create" scope="anywhere">
          <Button onClick={() => setDialog({ kind: "new", parentId: null })}>
            <Plus aria-hidden /> {t("add")}
          </Button>
        </Can>
      </div>

      {tree.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {tree.isLoading ? (
        <div aria-busy="true" className="space-y-2">
          <Skeleton className="h-14" />
          <Skeleton className="h-14" />
          <Skeleton className="h-14" />
        </div>
      ) : flat.length === 0 && !tree.isError ? (
        <Card>
          <EmptyState icon={<Layers />} title={t("emptyTitle")} description={t("emptyDescription")} />
        </Card>
      ) : (
        <Card className="p-3 sm:p-4">{renderLevel(tree.data ?? [], null)}</Card>
      )}

      <CategoryDialog state={dialog} flat={flat} tree={tree.data ?? []} onClose={() => setDialog(null)} />

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("deleteTitle")}
        description={t("deleteDescription", { name: deleting?.name ?? "" })}
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

function CategoryDialog({ state, flat, tree, onClose }: { state: DialogState; flat: Flat[]; tree: CategoryNode[]; onClose: () => void }) {
  const t = useTranslations("lookups.categories");
  const tc = useTranslations("lookups.common");
  const report = useReportError();
  const create = useCreateCategory();
  const update = useUpdateCategory();

  const editing = state?.kind === "edit" ? flat.find((f) => f.node.id === state.id) : undefined;
  const initialParent = state?.kind === "new" ? state.parentId : (editing?.parentId ?? null);
  const key = state ? (state.kind === "edit" ? `edit-${state.id}` : `new-${state.parentId ?? "root"}`) : "closed";

  // The form is reset every time the dialog opens for another category (the key changes).
  return (
    <Dialog open={state !== null} onOpenChange={(open) => !open && onClose()}>
      <ModalContent size="md" closeLabel={tc("close")}>
        {state ? (
          <CategoryForm
            key={key}
            title={state.kind === "edit" ? t("dialogEditTitle") : initialParent ? t("dialogChildTitle", { parent: flat.find((f) => f.node.id === initialParent)?.node.nameEn ?? "" }) : t("dialogNewTitle")}
            editing={editing}
            initialParent={initialParent}
            flat={flat}
            tree={tree}
            busy={create.isPending || update.isPending}
            onCancel={onClose}
            onSubmit={async (body) => {
              try {
                if (editing) await update.mutateAsync({ id: editing.node.id, body });
                else await create.mutateAsync(body);
                toast.success(editing ? tc("saved") : tc("created"));
                onClose();
                return {};
              } catch (error) {
                return report(error);
              }
            }}
          />
        ) : null}
      </ModalContent>
    </Dialog>
  );
}

function CategoryForm({
  title,
  editing,
  initialParent,
  flat,
  tree,
  busy,
  onCancel,
  onSubmit,
}: {
  title: string;
  editing?: Flat;
  initialParent: string | null;
  flat: Flat[];
  tree: CategoryNode[];
  busy: boolean;
  onCancel: () => void;
  onSubmit: (body: { nameEn: string; nameBn: string | null; slug: string | null; parentId: string | null; sortOrder: number; isActive: boolean }) => Promise<Record<string, string>>;
}) {
  const t = useTranslations("lookups.categories");
  const tc = useTranslations("lookups.common");
  const [nameEn, setNameEn] = React.useState(editing?.node.nameEn ?? "");
  const [nameBn, setNameBn] = React.useState(editing?.node.nameBn ?? "");
  const [slug, setSlug] = React.useState(editing?.node.slug ?? "");
  const [parentId, setParentId] = React.useState<string>(initialParent ?? "");
  const [sortOrder, setSortOrder] = React.useState(String(editing ? toNumber(editing.node.sortOrder) : 0));
  const [isActive, setIsActive] = React.useState(editing?.node.isActive ?? true);
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  // A category cannot move under itself or one of its own sub-categories.
  const blocked = React.useMemo(() => {
    if (!editing) return new Set<string>();
    const find = (nodes: CategoryNode[]): CategoryNode | undefined => nodes.reduce<CategoryNode | undefined>((found, n) => found ?? (n.id === editing.node.id ? n : find(n.children)), undefined);
    const self = find(tree);
    return new Set([editing.node.id, ...(self ? descendantIds(self) : [])]);
  }, [editing, tree]);

  const NONE = "__none";
  const options = [{ value: NONE, label: t("noParent") }, ...flat.filter((f) => !blocked.has(f.node.id)).map((f) => ({ value: f.node.id, label: f.path }))];

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!nameEn.trim()) {
      setErrors({ nameEn: tc("nameRequired") });
      return;
    }
    const result = await onSubmit({
      nameEn: nameEn.trim(),
      nameBn: nameBn.trim() || null,
      slug: slug.trim() || null,
      parentId: parentId || null,
      sortOrder: Number.parseInt(sortOrder, 10) || 0,
      isActive,
    });
    setErrors(result);
  };

  return (
    <form onSubmit={submit} noValidate className="space-y-5">
      <div>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{title}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("dialogDescription")}</DialogDescription>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tc("nameEn")} htmlFor="cat-name-en" error={errors.nameEn} required>
          <Input id="cat-name-en" value={nameEn} onChange={(e) => setNameEn(e.target.value)} autoComplete="off" aria-invalid={!!errors.nameEn} />
        </Field>
        <Field label={tc("nameBn")} htmlFor="cat-name-bn" error={errors.nameBn}>
          <Input id="cat-name-bn" value={nameBn} onChange={(e) => setNameBn(e.target.value)} autoComplete="off" lang="bn" />
        </Field>
        <Field label={tc("slug")} htmlFor="cat-slug" hint={tc("slugHint")} error={errors.slug}>
          <Input id="cat-slug" value={slug} onChange={(e) => setSlug(e.target.value)} autoCapitalize="none" autoComplete="off" />
        </Field>
        <Field label={t("sortOrder")} htmlFor="cat-sort" hint={t("sortOrderHint")} error={errors.sortOrder}>
          <Input id="cat-sort" type="number" inputMode="numeric" min={0} value={sortOrder} onChange={(e) => setSortOrder(e.target.value)} />
        </Field>
        <Field label={t("parent")} htmlFor="cat-parent" error={errors.parentId} className="sm:col-span-2">
          <Select
            id="cat-parent"
            value={parentId || NONE}
            onValueChange={(v) => setParentId(v === NONE ? "" : v)}
            options={options}
            placeholder={t("noParent")}
            searchPlaceholder={t("searchParent")}
            emptyText={tc("search")}
            searchable
          />
        </Field>
      </div>
      <ChoiceRow htmlFor="cat-active">
        <Switch id="cat-active" checked={isActive} onCheckedChange={setIsActive} />
        <span>
          <span className="block font-medium">{tc("isActive")}</span>
          <span className="block text-sm text-muted">{tc("isActiveHint")}</span>
        </span>
      </ChoiceRow>
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="secondary" onClick={onCancel}>
          {tc("cancel")}
        </Button>
        <Button type="submit" loading={busy}>
          {tc("save")}
        </Button>
      </div>
    </form>
  );
}
