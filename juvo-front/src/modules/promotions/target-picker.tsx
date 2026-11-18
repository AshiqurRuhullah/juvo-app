"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Layers, Plus, Search, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { useDebounced } from "@/lib/hooks";
import { cn } from "@/lib/utils";
import { useAllBrands, useCategoryTree, useProducts } from "@/modules/catalog/api";
import { flattenCategories } from "@/modules/catalog/category-options";
import { VariantPicker } from "@/modules/inventory/common";
import type { PromotionForm, TargetKind, TargetRole, TargetRow, TypeRule } from "./logic";

const NONE_CHOSEN: ReadonlySet<string> = new Set();
const KINDS: TargetKind[] = ["Product", "Variant", "Category", "Brand", "Everything"];

type Props = {
  form: PromotionForm;
  rule: TypeRule;
  disabled?: boolean;
  error?: string;
  onChange: (targets: TargetRow[]) => void;
};

/**
 * What a promotion applies to: one box per role (on sale / customer buys / customer gets / bundle items). Each box lists what was
 * chosen and has a picker for products, variants, categories, brands or everything. The catalog lists are the ones the catalog
 * screens use; the API checks every id again (PROMO.TARGET_NOT_FOUND).
 */
export function TargetsEditor({ form, rule, disabled, error, onChange }: Props) {
  const t = useTranslations("promotions.targets");
  if (rule.roles.length === 0) {
    return (
      <p className="text-sm text-muted" data-testid="targets-none">
        {t("none")}
      </p>
    );
  }
  return (
    <div className="space-y-4" data-testid="targets-editor">
      {rule.roles.map((role) => (
        <RoleBox key={role} role={role} type={form.type} rule={rule} rows={form.targets} disabled={disabled} onChange={onChange} />
      ))}
      {error ? (
        <p role="alert" className="text-sm font-medium text-danger" data-testid="targets-error">
          {error}
        </p>
      ) : null}
    </div>
  );
}

function RoleBox({ role, type, rule, rows, disabled, onChange }: { role: TargetRole; type: string; rule: TypeRule; rows: TargetRow[]; disabled?: boolean; onChange: (rows: TargetRow[]) => void }) {
  const t = useTranslations("promotions.targets");
  const [adding, setAdding] = React.useState(false);
  const mine = rows.filter((r) => r.role === role);
  const others = rows.filter((r) => r.role !== role);
  const kinds = KINDS.filter((k) => k !== "Everything" || (rule.everything && role === "Main"));
  const roleKey = `role.${type === "BuyXGetY" || type === "Bundle" ? type : "Main"}.${role}`;

  const has = (kind: TargetKind, id: string | null) => mine.some((r) => r.kind === kind && r.targetId === id);
  const add = (row: Omit<TargetRow, "role" | "quantity"> & { quantity?: string }) => {
    if (has(row.kind, row.targetId)) return;
    onChange([...others, ...mine, { ...row, role, quantity: row.quantity ?? "1" }]);
  };
  const remove = (index: number) => onChange([...others, ...mine.filter((_, i) => i !== index)]);
  const setQuantity = (index: number, quantity: string) => onChange([...others, ...mine.map((r, i) => (i === index ? { ...r, quantity } : r))]);

  return (
    <section className="rounded-card border border-line p-3" aria-label={t(roleKey)} data-testid={`targets-${role}`}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0">
          <h3 className="font-semibold text-strong">{t(roleKey)}</h3>
          <p className="text-sm text-muted">{t(`${roleKey}Hint`)}</p>
        </div>
        {!disabled ? (
          <Button variant="secondary" onClick={() => setAdding((v) => !v)} aria-expanded={adding} data-testid={`targets-add-${role}`}>
            <Plus aria-hidden /> {adding ? t("closePicker") : t("add")}
          </Button>
        ) : null}
      </div>

      {mine.length === 0 ? <p className="mt-3 text-sm text-muted">{t("empty")}</p> : null}
      <ul className="mt-3 divide-y divide-line" aria-label={t(roleKey)}>
        {mine.map((row, index) => (
          <li key={`${row.kind}-${row.targetId ?? "all"}`} className="flex flex-wrap items-center gap-2 py-1.5" data-testid="target-row">
            <Badge variant="outline">{t(`kind.${row.kind}`)}</Badge>
            <span className="min-w-0 flex-1 font-medium text-strong [overflow-wrap:anywhere]">{row.kind === "Everything" ? t("everything") : row.name || row.targetId}</span>
            {role === "BundleItem" ? (
              <label className="flex items-center gap-2 text-sm text-muted">
                {t("quantity")}
                <Input
                  type="text"
                  inputMode="numeric"
                  className="w-20 text-center"
                  value={row.quantity}
                  disabled={disabled}
                  onChange={(e) => setQuantity(index, e.target.value.replace(/[^0-9]/g, ""))}
                  aria-label={t("quantityOf", { name: row.name || t(`kind.${row.kind}`) })}
                />
              </label>
            ) : null}
            {!disabled ? (
              <Button variant="ghost" size="icon" onClick={() => remove(index)} aria-label={t("remove", { name: row.kind === "Everything" ? t("everything") : row.name || t(`kind.${row.kind}`) })}>
                <Trash2 aria-hidden />
              </Button>
            ) : null}
          </li>
        ))}
      </ul>

      {adding && !disabled ? <Picker kinds={kinds} role={role} has={has} onAdd={add} /> : null}
    </section>
  );
}

function Picker({ kinds, role, has, onAdd }: { kinds: TargetKind[]; role: TargetRole; has: (kind: TargetKind, id: string | null) => boolean; onAdd: (row: { kind: TargetKind; targetId: string | null; name: string }) => void }) {
  const t = useTranslations("promotions.targets");
  const [kind, setKind] = React.useState<TargetKind>(kinds[0]!);
  return (
    <div className="mt-3 space-y-3 rounded-control border border-line bg-sunken/40 p-3" data-testid={`picker-${role}`}>
      <div role="group" aria-label={t("kindLabel")} className="flex flex-wrap gap-2">
        {kinds.map((k) => (
          <button
            key={k}
            type="button"
            aria-pressed={kind === k}
            onClick={() => setKind(k)}
            data-testid={`kind-${role}-${k}`}
            className={cn("min-h-11 rounded-control border px-3 font-medium transition-colors", kind === k ? "border-primary bg-primary-soft text-strong" : "border-line bg-card text-muted [@media(hover:hover)]:hover:text-strong")}
          >
            {t(`kind.${k}`)}
          </button>
        ))}
      </div>
      {kind === "Product" ? <ProductSearch role={role} has={has} onAdd={onAdd} /> : null}
      {kind === "Variant" ? (
        <VariantPickerBridge role={role} has={has} onAdd={onAdd} />
      ) : null}
      {kind === "Category" ? <CategoryChoice has={has} onAdd={onAdd} /> : null}
      {kind === "Brand" ? <BrandChoice has={has} onAdd={onAdd} /> : null}
      {kind === "Everything" ? (
        <Button variant="secondary" disabled={has("Everything", null)} onClick={() => onAdd({ kind: "Everything", targetId: null, name: "" })} data-testid="add-everything">
          <Layers aria-hidden /> {t("addEverything")}
        </Button>
      ) : null}
    </div>
  );
}

function VariantPickerBridge({ role, has, onAdd }: { role: TargetRole; has: (kind: TargetKind, id: string | null) => boolean; onAdd: (row: { kind: TargetKind; targetId: string | null; name: string }) => void }) {
  // Adding a variant twice is ignored here (the shared picker would grey the button with a text that differs from its name).
  return (
    <VariantPicker
      idPrefix={`promo-${role}`}
      chosen={NONE_CHOSEN}
      onAdd={(v) => {
        if (has("Variant", v.variantId)) return;
        onAdd({ kind: "Variant", targetId: v.variantId, name: `${v.productName} (${v.sku})` });
      }}
    />
  );
}

type Add = (row: { kind: TargetKind; targetId: string | null; name: string }) => void;
type Has = (kind: TargetKind, id: string | null) => boolean;

function ProductSearch({ role, has, onAdd }: { role: TargetRole; has: Has; onAdd: Add }) {
  const t = useTranslations("promotions.targets");
  const [text, setText] = React.useState("");
  const term = useDebounced(text.trim(), 350);
  const id = `promo-product-${role}`;
  return (
    <div className="space-y-2">
      <label htmlFor={id} className="block text-sm font-semibold text-strong">
        {t("searchProduct")}
      </label>
      <div className="relative">
        <Search className="pointer-events-none absolute start-3.5 top-1/2 size-5 -translate-y-1/2 text-muted" aria-hidden />
        <Input id={id} type="search" value={text} onChange={(e) => setText(e.target.value)} placeholder={t("searchPlaceholder")} className="ps-11" autoComplete="off" />
      </div>
      {term.length < 2 ? <p className="text-sm text-muted">{t("searchHint")}</p> : <ProductHits term={term} has={has} onAdd={onAdd} />}
    </div>
  );
}

function ProductHits({ term, has, onAdd }: { term: string; has: Has; onAdd: Add }) {
  const t = useTranslations("promotions.targets");
  const query = useProducts({ page: 1, pageSize: 8, search: term });
  const items = query.data?.items ?? [];
  if (query.isLoading) return <Skeleton className="h-20" aria-busy="true" />;
  if (query.isError) return <Alert variant="danger">{t("searchFailed")}</Alert>;
  if (items.length === 0) return <p className="text-sm text-muted">{t("noProducts", { term })}</p>;
  return (
    <ul className="divide-y divide-line overflow-hidden rounded-control border border-line" aria-label={t("results")}>
      {items.map((p) => {
        const added = has("Product", p.id);
        return (
          <li key={p.id}>
            <button
              type="button"
              disabled={added}
              onClick={() => onAdd({ kind: "Product", targetId: p.id, name: p.nameEn })}
              data-testid="product-result"
              className="flex min-h-11 w-full items-center justify-between gap-2 bg-card px-4 py-2 text-start disabled:opacity-60 [@media(hover:hover)]:hover:bg-sunken"
            >
              <span className="font-semibold text-strong [overflow-wrap:anywhere]">{p.nameEn}</span>
              <span className="shrink-0 text-sm text-muted">{added ? t("added") : t("add")}</span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

function CategoryChoice({ has, onAdd }: { has: Has; onAdd: Add }) {
  const t = useTranslations("promotions.targets");
  const tc = useTranslations("promotions.common");
  const tree = useCategoryTree(false);
  const options = React.useMemo(() => flattenCategories(tree.data ?? []).map((c) => ({ value: c.id, label: c.label, disabled: has("Category", c.id) })), [tree.data, has]);
  if (tree.isLoading) return <Skeleton className="h-12" aria-busy="true" />;
  if (tree.isError) return <Alert variant="danger">{t("lookupFailed")}</Alert>;
  return (
    <Select
      aria-label={t("chooseCategory")}
      value={null}
      onValueChange={(v) => {
        const hit = options.find((o) => o.value === v);
        if (hit) onAdd({ kind: "Category", targetId: v, name: hit.label });
      }}
      options={options}
      placeholder={t("chooseCategory")}
      searchPlaceholder={tc("search")}
      emptyText={tc("noMatch")}
    />
  );
}

function BrandChoice({ has, onAdd }: { has: Has; onAdd: Add }) {
  const t = useTranslations("promotions.targets");
  const tc = useTranslations("promotions.common");
  const brands = useAllBrands();
  const options = React.useMemo(() => (brands.data?.items ?? []).map((b) => ({ value: b.id, label: b.nameEn, disabled: has("Brand", b.id) })), [brands.data, has]);
  if (brands.isLoading) return <Skeleton className="h-12" aria-busy="true" />;
  if (brands.isError) return <Alert variant="danger">{t("lookupFailed")}</Alert>;
  return (
    <Select
      aria-label={t("chooseBrand")}
      value={null}
      onValueChange={(v) => {
        const hit = options.find((o) => o.value === v);
        if (hit) onAdd({ kind: "Brand", targetId: v, name: hit.label });
      }}
      options={options}
      placeholder={t("chooseBrand")}
      searchPlaceholder={tc("search")}
      emptyText={tc("noMatch")}
    />
  );
}
