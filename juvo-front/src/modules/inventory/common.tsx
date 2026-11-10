"use client";

import * as React from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useTranslations } from "next-intl";
import { Plus, Search } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/form";
import { type SelectOption } from "@/components/ui/select";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { usePermissions, useSession } from "@/components/providers/session-provider";
import { useDebounced } from "@/lib/hooks";
import { cn } from "@/lib/utils";
import { useProduct, useProducts } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useProductStock, type CountStatus, type StockReason, type TransferStatus } from "./api";

/** The branches this person can choose between (their own, active ones). Used by filters and pickers. */
export function useBranchOptions(): SelectOption[] {
  const { me } = useSession();
  return React.useMemo(() => me.branches.filter((b) => b.isActive).map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` })), [me.branches]);
}

/** Moves between the inventory screens. Only the parts the person may view are shown. */
export function InventoryTabs() {
  const t = useTranslations("inventory.tabs");
  const pathname = usePathname();
  const { canAny } = usePermissions();
  const items = [
    { href: "/admin/inventory", label: t("stock"), show: canAny(["inventory.stock.view"], "anywhere") },
    { href: "/admin/inventory/movements", label: t("movements"), show: canAny(["inventory.stock.view"], "anywhere") },
    { href: "/admin/inventory/transfers", label: t("transfers"), show: canAny(["inventory.transfer.view"], "anywhere") },
    { href: "/admin/inventory/counts", label: t("counts"), show: canAny(["inventory.count.view"], "anywhere") },
  ].filter((item) => item.show);
  if (items.length <= 1) return null;

  return (
    <nav aria-label={t("label")} className="mb-5">
      <ul className={cn("grid gap-1 rounded-control bg-sunken p-1", items.length === 2 ? "grid-cols-2" : "grid-cols-2 md:grid-cols-4")}>
        {items.map((item) => {
          const active = item.href === "/admin/inventory" ? pathname === item.href : pathname === item.href || pathname.startsWith(`${item.href}/`);
          return (
            <li key={item.href} className="min-w-0">
              <Link
                href={item.href}
                aria-current={active ? "page" : undefined}
                className={cn(
                  "flex min-h-11 items-center justify-center rounded-lg px-3 text-center text-base font-semibold [overflow-wrap:anywhere] transition-colors",
                  active ? "bg-card text-strong shadow-sm" : "text-muted [@media(hover:hover)]:hover:text-strong",
                )}
              >
                {item.label}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}

/** Digits only (Bengali digits are turned into 0-9), so a phone's numeric keypad works in either language. */
export function digitsOnly(value: string): string {
  return value
    .replace(/[০-৯]/g, (ch) => String(ch.charCodeAt(0) - 0x09e6))
    .replace(/\D/g, "")
    .slice(0, 7);
}

export const QtyInput = React.forwardRef<HTMLInputElement, Omit<React.InputHTMLAttributes<HTMLInputElement>, "type" | "inputMode" | "value" | "onChange"> & { value: string; onValueChange: (value: string) => void }>(
  ({ value, onValueChange, className, ...props }, ref) => (
    <Input ref={ref} type="text" inputMode="numeric" pattern="[0-9]*" autoComplete="off" value={value} onChange={(e) => onValueChange(digitsOnly(e.target.value))} className={className} {...props} />
  ),
);
QtyInput.displayName = "QtyInput";

export function useReasonLabel() {
  const t = useTranslations("inventory.reasons");
  return (reason: StockReason) => t(reason);
}

const TRANSFER_VARIANT: Record<TransferStatus, "info" | "warning" | "success" | "danger" | "outline"> = {
  Requested: "info",
  Approved: "info",
  InTransit: "warning",
  Received: "success",
  Cancelled: "outline",
  Rejected: "danger",
};

export function TransferStatusBadge({ status }: { status: TransferStatus }) {
  const t = useTranslations("inventory.transferStatus");
  return <Badge variant={TRANSFER_VARIANT[status]}>{t(status)}</Badge>;
}

const COUNT_VARIANT: Record<CountStatus, "info" | "warning" | "success" | "outline"> = {
  InProgress: "info",
  Submitted: "warning",
  Approved: "success",
  Cancelled: "outline",
};

export function CountStatusBadge({ status }: { status: CountStatus }) {
  const t = useTranslations("inventory.countStatus");
  return <Badge variant={COUNT_VARIANT[status]}>{t(status)}</Badge>;
}

/** "SKU - colour - size" under a product name. */
export function VariantText({ sku, colorName, size, className }: { sku: string; colorName?: string | null; size?: string | null; className?: string }) {
  const parts = [colorName, size].filter(Boolean).join(" · ");
  return (
    <span className={cn("block text-sm font-normal text-muted [overflow-wrap:anywhere]", className)}>
      <span className="font-mono">{sku}</span>
      {parts ? ` · ${parts}` : ""}
    </span>
  );
}

export type PickedVariant = { variantId: string; productId: string; productName: string; sku: string; colorName: string | null; size: string | null };

/**
 * Finds variants to put on a transfer or a count: search a product, then add any of its variants. Shows the quantity available in
 * the source branch when the person may view it (the stock endpoint answers 403 or 404 otherwise, which is simply not shown).
 */
export function VariantPicker({ chosen, onAdd, sourceBranchId, idPrefix = "vp" }: { chosen: ReadonlySet<string>; onAdd: (variant: PickedVariant) => void; sourceBranchId?: string; idPrefix?: string }) {
  const t = useTranslations("inventory.picker");
  const [text, setText] = React.useState("");
  const term = useDebounced(text.trim(), 350);
  const [productId, setProductId] = React.useState<string | null>(null);
  const inputId = `${idPrefix}-search`;

  return (
    <div className="space-y-3">
      <div>
        <label htmlFor={inputId} className="mb-1.5 block text-sm font-semibold text-strong">
          {t("label")}
        </label>
        <div className="relative">
          <Search className="pointer-events-none absolute start-3.5 top-1/2 size-5 -translate-y-1/2 text-muted" aria-hidden />
          <Input id={inputId} type="search" value={text} onChange={(e) => { setText(e.target.value); setProductId(null); }} placeholder={t("placeholder")} className="ps-11" autoComplete="off" />
        </div>
      </div>
      {term.length >= 2 && !productId ? <ProductResults term={term} onPick={setProductId} /> : null}
      {productId ? <ProductVariants productId={productId} chosen={chosen} onAdd={onAdd} sourceBranchId={sourceBranchId} onBack={() => setProductId(null)} /> : null}
      {term.length < 2 && !productId ? <p className="text-sm text-muted">{t("hint")}</p> : null}
    </div>
  );
}

function ProductResults({ term, onPick }: { term: string; onPick: (id: string) => void }) {
  const t = useTranslations("inventory.picker");
  const query = useProducts({ page: 1, pageSize: 8, search: term });
  if (query.isLoading) return <Skeleton className="h-24" aria-busy="true" />;
  if (query.isError) return <Alert variant="danger">{t("searchFailed")}</Alert>;
  const items = query.data?.items ?? [];
  if (items.length === 0) return <p className="text-sm text-muted">{t("noProducts", { term })}</p>;
  return (
    <ul className="divide-y divide-line overflow-hidden rounded-control border border-line" aria-label={t("results")}>
      {items.map((p) => (
        <li key={p.id}>
          <button type="button" onClick={() => onPick(p.id)} className="flex min-h-11 w-full flex-col items-start justify-center gap-0.5 bg-card px-4 py-2 text-start [@media(hover:hover)]:hover:bg-sunken">
            <span className="font-semibold text-strong [overflow-wrap:anywhere]">{p.nameEn}</span>
            <span className="text-sm text-muted">{t("variantCount", { count: toNumber(p.variantCount) })}</span>
          </button>
        </li>
      ))}
    </ul>
  );
}

function ProductVariants({ productId, chosen, onAdd, sourceBranchId, onBack }: { productId: string; chosen: ReadonlySet<string>; onAdd: (variant: PickedVariant) => void; sourceBranchId?: string; onBack: () => void }) {
  const t = useTranslations("inventory.picker");
  const product = useProduct(productId);
  const stock = useProductStock(sourceBranchId ? productId : undefined);
  if (product.isLoading) return <Skeleton className="h-24" aria-busy="true" />;
  if (product.isError || !product.data) return <Alert variant="danger">{t("searchFailed")}</Alert>;
  const p = product.data;
  const variants = p.variants.filter((v) => v.isActive && v.tracksStock);

  const availableOf = (variantId: string) =>
    toNumberOrNull(stock.data?.variants.find((v) => v.variantId === variantId)?.branches.find((b) => b.branchId === sourceBranchId)?.available);

  return (
    <div className="space-y-2 rounded-control border border-line p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="font-semibold text-strong [overflow-wrap:anywhere]">{p.nameEn}</p>
        <Button variant="ghost" onClick={onBack}>
          {t("otherProduct")}
        </Button>
      </div>
      {variants.length === 0 ? <p className="text-sm text-muted">{t("noVariants")}</p> : null}
      <ul className="divide-y divide-line">
        {variants.map((v) => {
          const available = availableOf(v.id);
          const added = chosen.has(v.id);
          return (
            <li key={v.id} className="flex flex-wrap items-center justify-between gap-2 py-1.5">
              <div className="min-w-0">
                <VariantText sku={v.sku} colorName={v.colorName} size={v.size} className="text-base text-strong" />
                {available !== null ? <span className="text-sm text-muted">{t("available", { count: available })}</span> : null}
              </div>
              <Button variant="secondary" disabled={added} aria-label={t("addVariant", { sku: v.sku })} onClick={() => onAdd({ variantId: v.id, productId: p.id, productName: p.nameEn, sku: v.sku, colorName: v.colorName ?? null, size: v.size ?? null })}>
                <Plus aria-hidden /> {added ? t("added") : t("add")}
              </Button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

function toNumberOrNull(value: number | string | null | undefined): number | null {
  return value === null || value === undefined ? null : toNumber(value);
}
