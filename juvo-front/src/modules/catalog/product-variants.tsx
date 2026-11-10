"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Pencil, Plus, Trash2, Wand2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input, Label, Switch } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useTableLabels } from "@/components/data/list-kit";
import { Can } from "@/components/providers/session-provider";
import { useAddVariant, useRemoveVariant, useReportError, useUpdateVariant, type ProductDetail, type ProductStock, type SaveVariant, type Variant } from "./api";
import { toNumber, useMoney } from "./format";

/** Common colour names to a swatch, so typing "Black, White" in the generator gives swatches without picking each one. */
const COLOUR_HEX: Record<string, string> = {
  black: "#1C1C1C", white: "#F4F1EC", red: "#C62828", blue: "#2F5D9B", navy: "#1F2F55", green: "#2E7D5B", brown: "#6B4423", beige: "#D9C7A3", cream: "#EDE3D0",
  grey: "#8A8A8A", gray: "#8A8A8A", pink: "#D98294", rose: "#D98294", maroon: "#6E1423", wine: "#5B1030", teal: "#1E7F7A", emerald: "#0F7B54", khaki: "#B8A67A",
  tan: "#B07B4A", gold: "#C9A24B", silver: "#B5B7BB", yellow: "#E3B93C", orange: "#D9772B", purple: "#6B3FA0",
};

const HEX = /^#[0-9A-Fa-f]{6}$/;

type VariantForm = { sku: string; colorName: string; colorHex: string; size: string; priceOverride: string; barcode: string; weightGramsOverride: string; isActive: boolean };

const emptyForm = (): VariantForm => ({ sku: "", colorName: "", colorHex: "", size: "", priceOverride: "", barcode: "", weightGramsOverride: "", isActive: true });

const formFrom = (v: Variant): VariantForm => ({
  sku: v.sku, colorName: v.colorName ?? "", colorHex: v.colorHex ?? "", size: v.size ?? "", priceOverride: v.priceOverride === null ? "" : String(v.priceOverride),
  barcode: v.barcode ?? "", weightGramsOverride: v.weightGramsOverride === null ? "" : String(v.weightGramsOverride), isActive: v.isActive,
});

const toRequest = (f: VariantForm, sortOrder: number): SaveVariant => ({
  sku: f.sku.trim(),
  colorName: f.colorName.trim() || null,
  colorHex: f.colorHex.trim() || null,
  size: f.size.trim() || null,
  priceOverride: f.priceOverride.trim() ? Number(f.priceOverride.trim().replace(",", ".")) : null,
  barcode: f.barcode.trim() || null,
  weightGramsOverride: f.weightGramsOverride.trim() ? Math.trunc(Number(f.weightGramsOverride.trim())) : null,
  isActive: f.isActive,
  sortOrder,
});

/** Add or edit one variant: SKU, colour with swatch, size, price override, barcode, weight (FR-B02). */
function VariantBody({ onOpenChange, product, variant }: { onOpenChange: (open: boolean) => void; product: ProductDetail; variant?: Variant }) {
  const t = useTranslations("catalog.variants");
  const tc = useTranslations("catalog.common");
  const report = useReportError();
  const add = useAddVariant(product.id);
  const update = useUpdateVariant(product.id);
  const [form, setForm] = React.useState<VariantForm>(() => (variant ? formFrom(variant) : emptyForm()));
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  const set = <K extends keyof VariantForm>(key: K, value: VariantForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const save = async () => {
    const next: Record<string, string> = {};
    if (!form.sku.trim()) next.sku = t("skuRequired");
    if (form.colorHex.trim() && !HEX.test(form.colorHex.trim())) next.colorHex = t("hexInvalid");
    if (form.priceOverride.trim() && !/^\d+([.,]\d{1,2})?$/.test(form.priceOverride.trim())) next.priceOverride = t("amountInvalid");
    if (form.weightGramsOverride.trim() && !/^\d+$/.test(form.weightGramsOverride.trim())) next.weightGramsOverride = t("wholeInvalid");
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    try {
      if (variant) await update.mutateAsync({ variantId: variant.id, body: toRequest(form, toNumber(variant.sortOrder)) });
      else await add.mutateAsync(toRequest(form, product.variants.length));
      toast.success(variant ? t("updated") : t("added"));
      onOpenChange(false);
    } catch (error) {
      const fields = report(error);
      setErrors({ sku: fields.sku ?? "", colorHex: fields.colorHex ?? "", priceOverride: fields.priceOverride ?? "" });
    }
  };

  const busy = add.isPending || update.isPending;
  const swatch = HEX.test(form.colorHex.trim()) ? form.colorHex.trim() : "#000000";

  return (
    <>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{variant ? t("editTitle") : t("addTitle")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("dialogHint")}</DialogDescription>
        <div className="mt-4 grid gap-4 sm:grid-cols-2">
          <Field label={t("sku")} htmlFor="v-sku" error={errors.sku || undefined} hint={t("skuHint")} required className="sm:col-span-2">
            <Input id="v-sku" value={form.sku} onChange={(e) => set("sku", e.target.value)} maxLength={60} autoComplete="off" aria-invalid={!!errors.sku} />
          </Field>
          <Field label={t("colour")} htmlFor="v-colour">
            <Input id="v-colour" value={form.colorName} onChange={(e) => set("colorName", e.target.value)} maxLength={60} autoComplete="off" />
          </Field>
          <Field label={t("swatch")} htmlFor="v-hex" error={errors.colorHex || undefined}>
            <div className="flex items-center gap-2">
              <input
                type="color"
                aria-label={t("pickColour")}
                value={swatch}
                onChange={(e) => set("colorHex", e.target.value.toUpperCase())}
                className="size-12 shrink-0 cursor-pointer rounded-control border border-line bg-card p-1"
              />
              <Input id="v-hex" value={form.colorHex} onChange={(e) => set("colorHex", e.target.value)} placeholder="#1C0807" maxLength={7} autoComplete="off" aria-invalid={!!errors.colorHex} />
            </div>
          </Field>
          <Field label={t("size")} htmlFor="v-size">
            <Input id="v-size" value={form.size} onChange={(e) => set("size", e.target.value)} maxLength={30} autoComplete="off" />
          </Field>
          <Field label={t("priceOverride")} htmlFor="v-price" hint={t("priceOverrideHint")} error={errors.priceOverride || undefined}>
            <Input id="v-price" value={form.priceOverride} onChange={(e) => set("priceOverride", e.target.value)} inputMode="decimal" autoComplete="off" aria-invalid={!!errors.priceOverride} />
          </Field>
          <Field label={t("barcode")} htmlFor="v-barcode">
            <Input id="v-barcode" value={form.barcode} onChange={(e) => set("barcode", e.target.value)} maxLength={60} autoComplete="off" />
          </Field>
          <Field label={t("weightOverride")} htmlFor="v-weight" error={errors.weightGramsOverride || undefined}>
            <Input id="v-weight" value={form.weightGramsOverride} onChange={(e) => set("weightGramsOverride", e.target.value)} inputMode="numeric" autoComplete="off" aria-invalid={!!errors.weightGramsOverride} />
          </Field>
          <div className="flex items-start justify-between gap-4 sm:col-span-2">
            <div>
              <Label htmlFor="v-active">{t("active")}</Label>
              <p className="text-sm text-muted">{t("activeHint")}</p>
            </div>
            <Switch id="v-active" checked={form.isActive} onCheckedChange={(v) => set("isActive", v)} className="mt-1" />
          </div>
        </div>
        <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={busy}>
            {tc("cancel")}
          </Button>
          <Button onClick={() => void save()} loading={busy}>
            {variant ? tc("save") : t("add")}
          </Button>
        </div>
    </>
  );
}

function VariantDialog({ open, onOpenChange, product, variant }: { open: boolean; onOpenChange: (open: boolean) => void; product: ProductDetail; variant?: Variant }) {
  const tc = useTranslations("catalog.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent closeLabel={tc("close")}>{open ? <VariantBody onOpenChange={onOpenChange} product={product} variant={variant} /> : null}</ModalContent>
    </Dialog>
  );
}

/** Makes every colour x size combination in one go (the usual way fashion variants are entered). */
function GenerateBody({ onOpenChange, product }: { onOpenChange: (open: boolean) => void; product: ProductDetail }) {
  const t = useTranslations("catalog.variants");
  const tc = useTranslations("catalog.common");
  const report = useReportError();
  const add = useAddVariant(product.id);
  const [prefix, setPrefix] = React.useState(() => product.slug.toUpperCase().replace(/[^A-Z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 16));
  const [colours, setColours] = React.useState("");
  const [sizes, setSizes] = React.useState("");
  const [running, setRunning] = React.useState(false);

  const split = (value: string) => [...new Set(value.split(/[,\n;]/).map((s) => s.trim()).filter(Boolean))];
  const colourList = split(colours);
  const sizeList = split(sizes);
  const existing = new Set(product.variants.map((v) => `${(v.colorName ?? "").toLowerCase()}|${(v.size ?? "").toLowerCase()}`));
  const skuOf = (colour: string, size: string) => [prefix.trim().toUpperCase(), colour.replace(/[^A-Za-z0-9]/g, "").slice(0, 3).toUpperCase(), size.replace(/[^A-Za-z0-9]/g, "").toUpperCase()].filter(Boolean).join("-");
  const combos = (colourList.length ? colourList : [""]).flatMap((colour) => (sizeList.length ? sizeList : [""]).map((size) => ({ colour, size })))
    .filter((c) => (c.colour || c.size) && !existing.has(`${c.colour.toLowerCase()}|${c.size.toLowerCase()}`));

  const run = async () => {
    setRunning(true);
    let order = product.variants.length;
    let made = 0;
    try {
      for (const combo of combos) {
        await add.mutateAsync({
          sku: skuOf(combo.colour, combo.size), colorName: combo.colour || null, colorHex: COLOUR_HEX[combo.colour.toLowerCase()] ?? null, size: combo.size || null,
          priceOverride: null, barcode: null, weightGramsOverride: null, isActive: true, sortOrder: order++,
        });
        made++;
      }
      toast.success(t("generated", { count: made }));
      onOpenChange(false);
    } catch (error) {
      report(error);
      if (made > 0) toast.info(t("generatedPartly", { count: made }));
    } finally {
      setRunning(false);
    }
  };

  return (
    <>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("generateTitle")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("generateHint")}</DialogDescription>
        <div className="mt-4 grid gap-4">
          <Field label={t("skuPrefix")} htmlFor="g-prefix" hint={t("skuPrefixHint")}>
            <Input id="g-prefix" value={prefix} onChange={(e) => setPrefix(e.target.value)} maxLength={30} autoComplete="off" />
          </Field>
          <Field label={t("coloursList")} htmlFor="g-colours" hint={t("coloursListHint")}>
            <Input id="g-colours" value={colours} onChange={(e) => setColours(e.target.value)} placeholder="Black, White, Red" autoComplete="off" />
          </Field>
          <Field label={t("sizesList")} htmlFor="g-sizes" hint={t("sizesListHint")}>
            <Input id="g-sizes" value={sizes} onChange={(e) => setSizes(e.target.value)} placeholder="S, M, L, XL" autoComplete="off" />
          </Field>
          <div aria-live="polite" className="rounded-control bg-sunken p-3 text-sm">
            {combos.length === 0 ? (
              <span className="text-muted">{t("nothingToMake")}</span>
            ) : (
              <>
                <p className="font-semibold text-strong">{t("willMake", { count: combos.length })}</p>
                <p className="mt-1 text-muted [overflow-wrap:anywhere]">
                  {combos.slice(0, 6).map((c) => skuOf(c.colour, c.size)).join(", ")}
                  {combos.length > 6 ? ` … +${combos.length - 6}` : ""}
                </p>
              </>
            )}
          </div>
        </div>
        <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={running}>
            {tc("cancel")}
          </Button>
          <Button onClick={() => void run()} loading={running} disabled={combos.length === 0 || combos.length > 200 || !prefix.trim()}>
            {t("generate")}
          </Button>
        </div>
    </>
  );
}

function GenerateDialog({ open, onOpenChange, product }: { open: boolean; onOpenChange: (open: boolean) => void; product: ProductDetail }) {
  const tc = useTranslations("catalog.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent closeLabel={tc("close")}>{open ? <GenerateBody onOpenChange={onOpenChange} product={product} /> : null}</ModalContent>
    </Dialog>
  );
}

/** The variants of a product: list, add, generate, edit, remove. Stock numbers come from the inventory grid when the user may see them. */
export function ProductVariants({ product, stock, disabled }: { product: ProductDetail; stock?: ProductStock; disabled?: boolean }) {
  const t = useTranslations("catalog.variants");
  const tc = useTranslations("catalog.common");
  const labels = useTableLabels();
  const money = useMoney();
  const report = useReportError();
  const remove = useRemoveVariant(product.id);
  const [dialog, setDialog] = React.useState<{ open: boolean; variant?: Variant }>({ open: false });
  const [generating, setGenerating] = React.useState(false);
  const [removing, setRemoving] = React.useState<Variant | null>(null);

  const stockOf = React.useMemo(() => new Map((stock?.variants ?? []).map((v) => [v.variantId, v])), [stock]);

  const columns = React.useMemo<ColumnDef<Variant, unknown>[]>(
    () => [
      {
        id: "sku",
        header: t("sku"),
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="flex flex-col">
            <span className="font-mono text-sm font-semibold [overflow-wrap:anywhere]">{row.original.sku}</span>
            {row.original.barcode ? <span className="text-xs font-normal text-muted">{row.original.barcode}</span> : null}
          </span>
        ),
      },
      {
        id: "colour",
        header: t("colour"),
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.colorName || row.original.colorHex ? (
            <span className="inline-flex items-center gap-2">
              <span className="size-4 shrink-0 rounded-full border border-line" style={{ backgroundColor: row.original.colorHex ?? "transparent" }} aria-hidden />
              {row.original.colorName}
            </span>
          ) : (
            <span className="text-muted">-</span>
          ),
      },
      { id: "size", header: t("size"), meta: { secondary: true }, cell: ({ row }) => row.original.size ?? <span className="text-muted">-</span> },
      {
        id: "price",
        header: t("priceOverride"),
        meta: { secondary: true },
        cell: ({ row }) => (row.original.priceOverride === null ? <span className="text-muted">{t("sameAsProduct")}</span> : money(row.original.priceOverride)),
      },
      {
        id: "stock",
        header: t("stock"),
        meta: { secondary: true },
        cell: ({ row }) => {
          const s = stockOf.get(row.original.id);
          return s ? <span className="font-semibold">{toNumber(s.onHand)}</span> : <span className="text-muted">-</span>;
        },
      },
      {
        id: "status",
        header: t("status"),
        meta: { secondary: true },
        cell: ({ row }) => (row.original.isActive ? <Badge variant="success">{t("active")}</Badge> : <Badge variant="outline">{t("inactive")}</Badge>),
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: tc("actions") },
        cell: ({ row }) =>
          disabled ? null : (
            <span className="flex gap-1">
              <Can permission="catalog.product.edit" scope="anywhere">
                <Button variant="ghost" size="icon" aria-label={t("edit", { sku: row.original.sku })} onClick={() => setDialog({ open: true, variant: row.original })}>
                  <Pencil aria-hidden />
                </Button>
                <Button variant="ghost" size="icon" aria-label={t("remove", { sku: row.original.sku })} onClick={() => setRemoving(row.original)}>
                  <Trash2 aria-hidden />
                </Button>
              </Can>
            </span>
          ),
      },
    ],
    [t, tc, money, stockOf, disabled],
  );

  const sorted = React.useMemo(() => [...product.variants].sort((a, b) => toNumber(a.sortOrder) - toNumber(b.sortOrder) || a.sku.localeCompare(b.sku)), [product.variants]);

  return (
    <div className="space-y-5">
      <Card>
        <CardHeader className="gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <CardTitle>{t("title")}</CardTitle>
            <CardDescription>{t("subtitle")}</CardDescription>
          </div>
          {!disabled ? (
            <Can permission="catalog.product.edit" scope="anywhere">
              <div className="flex flex-col gap-2 sm:flex-row sm:flex-wrap">
                <Button variant="secondary" onClick={() => setGenerating(true)}>
                  <Wand2 aria-hidden /> {t("generate")}
                </Button>
                <Button onClick={() => setDialog({ open: true })}>
                  <Plus aria-hidden /> {t("add")}
                </Button>
              </div>
            </Can>
          ) : null}
        </CardHeader>
        <CardContent>
          {sorted.length === 0 ? <Alert variant="info">{t("none")}</Alert> : null}
          {sorted.length > 0 ? <DataTable caption={t("title")} columns={columns} data={sorted} getRowId={(v) => v.id} labels={labels} total={sorted.length} pageSize={Math.max(sorted.length, 1)} /> : null}
        </CardContent>
      </Card>

      <VariantDialog open={dialog.open} onOpenChange={(open) => setDialog((d) => ({ ...d, open }))} product={product} variant={dialog.variant} />
      <GenerateDialog open={generating} onOpenChange={setGenerating} product={product} />
      <ConfirmDialog
        open={!!removing}
        onOpenChange={(open) => !open && setRemoving(null)}
        title={t("removeTitle")}
        description={t("removeDescription", { sku: removing?.sku ?? "" })}
        confirmLabel={t("removeConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={async () => {
          if (!removing) return;
          try {
            await remove.mutateAsync(removing.id);
            toast.success(t("removed"));
            setRemoving(null);
          } catch (error) {
            report(error);
            setRemoving(null);
          }
        }}
      />
    </div>
  );
}
