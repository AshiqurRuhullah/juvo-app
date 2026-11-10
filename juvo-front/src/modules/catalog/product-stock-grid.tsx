"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowRight } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/form";
import { Alert, Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useSession } from "@/components/providers/session-provider";
import { canInBranch } from "@/lib/auth/permissions";
import { useAdjustStock, useReportError, type ProductDetail, type ProductStock, type StockReason } from "./api";
import { toNumber } from "./format";

type Key = `${string}|${string}`;

/**
 * Stock of every variant in every branch the user may view, with a quick "set quantity" for branches where they may adjust stock
 * (FR-V03, 4A.2). Saving works out the difference and books it in the ledger: opening stock for a first quantity, a correction
 * after that. Reserved units (promised to orders) cannot be taken away; the API refuses and says so.
 */
export function ProductStockGrid({ product, stock }: { product: ProductDetail; stock: ProductStock }) {
  const t = useTranslations("catalog.stock");
  const { me } = useSession();
  const report = useReportError();
  const adjust = useAdjustStock();
  const [edits, setEdits] = React.useState<Record<Key, string>>({});
  const [saving, setSaving] = React.useState(false);

  const variants = stock.variants.filter((v) => v.tracksStock);
  const keyOf = (variantId: string, branchId: string): Key => `${variantId}|${branchId}`;
  const parse = (text: string) => (/^\d{1,7}$/.test(text.trim()) ? Number(text.trim()) : null);

  const changes = variants.flatMap((v) =>
    v.branches.flatMap((b) => {
      const text = edits[keyOf(v.variantId, b.branchId)];
      if (text === undefined) return [];
      const target = parse(text);
      const current = toNumber(b.onHand);
      return target === null || target === current ? [] : [{ variantId: v.variantId, branchId: b.branchId, delta: target - current, opening: current === 0 && target > 0 }];
    }),
  );
  const invalid = Object.values(edits).some((text) => parse(text) === null);

  const save = async () => {
    setSaving(true);
    try {
      const groups = new Map<string, { branchId: string; reason: StockReason; lines: { variantId: string; quantityChange: number }[] }>();
      for (const c of changes) {
        const reason: StockReason = c.opening ? "OpeningStock" : "Correction";
        const key = `${c.branchId}|${reason}`;
        const group = groups.get(key) ?? { branchId: c.branchId, reason, lines: [] };
        group.lines.push({ variantId: c.variantId, quantityChange: c.delta });
        groups.set(key, group);
      }
      for (const group of groups.values()) {
        await adjust.mutateAsync({ branchId: group.branchId, reason: group.reason, note: t("note", { name: product.nameEn }), lines: group.lines });
      }
      toast.success(t("saved"));
      setEdits({});
    } catch (error) {
      report(error);
    } finally {
      setSaving(false);
    }
  };

  if (variants.length === 0) return <Alert variant="info">{t("noVariants")}</Alert>;

  return (
    <Card>
      <CardHeader className="gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <CardTitle>{t("title")}</CardTitle>
          <CardDescription>{t("subtitle")}</CardDescription>
        </div>
        <Button asChild variant="secondary">
          <Link href={`/admin/inventory?search=${encodeURIComponent(product.slug)}`}>
            {t("manage")} <ArrowRight className="rtl:rotate-180" aria-hidden />
          </Link>
        </Button>
      </CardHeader>
      <CardContent className="space-y-4">
        <ul className="space-y-3">
          {variants.map((v) => (
            <li key={v.variantId}>
              <fieldset className="rounded-control border border-line p-4">
                <legend className="px-2 text-sm font-semibold text-strong">
                  <span className="font-mono">{v.sku}</span>
                  {v.colorName || v.size ? <span className="ms-2 font-normal text-muted">{[v.colorName, v.size].filter(Boolean).join(" · ")}</span> : null}
                </legend>
                <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
                  {v.branches.map((b) => {
                    const id = `stock-${v.variantId}-${b.branchId}`;
                    const may = canInBranch(me, "inventory.stock.adjust", b.branchId);
                    const edit = edits[keyOf(v.variantId, b.branchId)];
                    return (
                      <div key={b.branchId} className="space-y-1">
                        <label htmlFor={id} className="flex flex-wrap items-center gap-2 text-sm font-semibold text-strong">
                          <span className="font-mono">{b.branchCode}</span>
                          <span className="font-normal text-muted [overflow-wrap:anywhere]">{b.branchName}</span>
                          {b.isLow ? <Badge variant="warning">{t("low")}</Badge> : null}
                        </label>
                        <Input
                          id={id}
                          value={edit ?? String(toNumber(b.onHand))}
                          onChange={(e) => setEdits((old) => ({ ...old, [keyOf(v.variantId, b.branchId)]: e.target.value }))}
                          inputMode="numeric"
                          disabled={!may || saving}
                          aria-invalid={edit !== undefined && parse(edit) === null}
                          autoComplete="off"
                        />
                        <p className="text-sm text-muted">{t("reservedAvailable", { reserved: toNumber(b.reserved), available: toNumber(b.available) })}</p>
                      </div>
                    );
                  })}
                </div>
              </fieldset>
            </li>
          ))}
        </ul>
        {invalid ? <Alert variant="danger">{t("invalid")}</Alert> : null}
        {changes.length > 0 || Object.keys(edits).length > 0 ? (
          <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
            <p aria-live="polite" className="text-sm text-muted">{t("pending", { count: changes.length })}</p>
            <div className="flex gap-2">
              <Button variant="secondary" onClick={() => setEdits({})} disabled={saving}>
                {t("discard")}
              </Button>
              <Button onClick={() => void save()} loading={saving} disabled={changes.length === 0 || invalid}>
                {t("save")}
              </Button>
            </div>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}
