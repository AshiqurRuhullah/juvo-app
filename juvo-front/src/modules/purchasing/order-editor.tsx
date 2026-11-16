"use client";

import * as React from "react";
import Link from "next/link";
import dynamic from "next/dynamic";
import { useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { AttachmentPicker, type UploadedFile } from "@/components/data/attachment-picker";
import { PageHeader } from "@/components/layout/page-header";
import { usePermissions } from "@/components/providers/session-provider";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useBranchOptions, VariantText, type PickedVariant } from "@/modules/inventory/common";
import { receiptProblem } from "@/modules/accounting/logic";
import { uploadPurchaseFile, usePurchaseOrder, useSavePurchaseOrder, useSupplierOptions, type PurchaseDetail } from "./api";
import { useMoney2 } from "./common";
import { draftProblem, draftTotal, lineTotal, parseAmount, parseQuantity, round2, type DraftLine } from "./logic";
import { takeSeed, type PoSeed } from "./seed";

// The product search only loads when the person opens the editor; it is not part of the list pages.
const VariantPicker = dynamic(() => import("@/modules/inventory/common").then((m) => m.VariantPicker), { ssr: false, loading: () => <Skeleton className="h-12" aria-busy="true" /> });

type Row = PickedVariant & DraftLine;

/** `/admin/purchasing/orders/new` and `/orders/[id]/edit`: create or change a draft purchase order. */
export function OrderEditor({ id }: { id?: string }) {
  const t = useTranslations("purchasing.editor");
  const tc = useTranslations("purchasing.common");
  const order = usePurchaseOrder(id);
  if (id && order.isLoading) return <Skeleton className="h-96" aria-busy="true" />;
  if (id && (order.isError || !order.data)) return <Alert variant="danger">{tc("loadFailed")}</Alert>;
  if (order.data && order.data.status !== "Draft") {
    return (
      <Alert variant="warning">
        {t("notDraft")}{" "}
        <Link href={`/admin/purchasing/orders/${order.data.id}`} className="font-semibold underline">
          {t("backToOrder")}
        </Link>
      </Alert>
    );
  }
  return <EditorForm key={order.data?.id ?? "new"} existing={order.data ?? null} />;
}

function EditorForm({ existing }: { existing: PurchaseDetail | null }) {
  const t = useTranslations("purchasing.editor");
  const tc = useTranslations("purchasing.common");
  const tn = useTranslations("nav");
  const router = useRouter();
  const params = useSearchParams();
  const report = useReportError();
  const money = useMoney2();
  const branches = useBranchOptions();
  const { activeBranchId } = usePermissions();
  const suppliers = useSupplierOptions();
  const save = useSavePurchaseOrder();

  // Lines handed over from the suggestions tab are read once, when the form opens.
  const [seed] = React.useState<PoSeed | null>(() => (!existing && params.get("seed") ? takeSeed() : null));
  const [supplierId, setSupplierId] = React.useState(existing?.supplierId ?? seed?.supplierId ?? "");
  const [branchId, setBranchId] = React.useState(existing?.branchId ?? seed?.branchId ?? (activeBranchId && branches.some((b) => b.value === activeBranchId) ? activeBranchId : (branches[0]?.value ?? "")));
  const [expected, setExpected] = React.useState(existing?.expectedDate ?? "");
  const [notes, setNotes] = React.useState(existing?.notes ?? "");
  const [rows, setRows] = React.useState<Row[]>(() =>
    existing
      ? existing.lines.map((l) => ({ variantId: l.variantId, productId: l.productId, productName: l.productName, sku: l.sku, colorName: l.colorName ?? null, size: l.size ?? null, quantity: String(toNumber(l.orderedQuantity)), unitCost: String(toNumber(l.unitCost)) }))
      : (seed?.lines ?? []),
  );
  const [added, setAdded] = React.useState<UploadedFile[]>([]);
  const [errors, setErrors] = React.useState<{ supplier?: string; branch?: string; lines?: string }>({});

  const chosen = React.useMemo(() => new Set(rows.map((r) => r.variantId)), [rows]);
  const total = React.useMemo(() => draftTotal(rows), [rows]);

  const addVariant = React.useCallback((v: PickedVariant) => {
    setErrors((e) => ({ ...e, lines: undefined }));
    setRows((old) => (old.some((r) => r.variantId === v.variantId) ? old : [...old, { ...v, quantity: "1", unitCost: "" }]));
  }, []);
  const change = React.useCallback((variantId: string, patch: Partial<DraftLine>) => setRows((old) => old.map((r) => (r.variantId === variantId ? { ...r, ...patch } : r))), []);
  const remove = React.useCallback((variantId: string) => setRows((old) => old.filter((r) => r.variantId !== variantId)), []);

  const supplierOptions = React.useMemo(() => {
    const list = (suppliers.data?.items ?? []).map((s) => ({ value: s.id, label: s.name }));
    // The supplier of an existing draft may have been switched off since: keep it visible so the form does not silently change it.
    if (existing && !list.some((o) => o.value === existing.supplierId)) list.push({ value: existing.supplierId, label: existing.supplierName });
    return list;
  }, [suppliers.data?.items, existing]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const problem = draftProblem({ supplierId, branchId, lines: rows });
    const next: typeof errors = {};
    if (problem === "supplier") next.supplier = t("errors.supplier");
    else if (problem === "branch") next.branch = t("errors.branch");
    else if (problem) next.lines = t(`errors.${problem}`);
    setErrors(next);
    if (problem) return;
    try {
      const saved = await save.mutateAsync({
        id: existing?.id,
        body: {
          branchId: existing ? null : branchId,
          supplierId,
          expectedDate: expected || null,
          notes: notes.trim() || null,
          lines: rows.map((r) => ({ variantId: r.variantId, quantity: parseQuantity(r.quantity)!, unitCost: round2(parseAmount(r.unitCost)!) })),
          attachmentPaths: added.map((f) => f.path!).filter(Boolean),
        },
      });
      toast.success(existing ? t("saved") : t("created"));
      router.push(`/admin/purchasing/orders/${saved.id}`);
    } catch (e) {
      report(e);
    }
  };

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.purchasing"), href: "/admin/purchasing" }, { label: existing ? existing.number : t("new") }];
  const attachLabels = {
    add: t("attachAdd"),
    uploading: t("attachUploading"),
    remove: (name: string) => t("attachRemove", { name }),
    hint: t("attachHint"),
    typeError: t("attachType"),
    sizeError: t("attachSize"),
    open: (name: string) => t("attachOpen", { name }),
  };

  return (
    <>
      <PageHeader title={existing ? t("editTitle", { number: existing.number }) : t("title")} description={t("subtitle")} crumbs={crumbs} />
      <form onSubmit={(e) => void submit(e)} noValidate className="space-y-6">
        <Card>
          <CardHeader>
            <CardTitle>{t("details")}</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            <Field label={t("supplier")} htmlFor="po-supplier" error={errors.supplier} required>
              <Select id="po-supplier" value={supplierId} onValueChange={setSupplierId} options={supplierOptions} placeholder={t("chooseSupplier")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.supplier} />
            </Field>
            <Field label={t("branch")} htmlFor="po-branch" error={errors.branch} hint={existing ? t("branchLocked") : undefined} required>
              <Select id="po-branch" value={branchId} onValueChange={setBranchId} options={branches} placeholder={t("chooseBranch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.branch} disabled={!!existing} />
            </Field>
            <Field label={t("expected")} htmlFor="po-expected">
              <Input id="po-expected" type="date" value={expected} onChange={(e) => setExpected(e.target.value)} />
            </Field>
            <Field label={t("notes")} htmlFor="po-notes" className="sm:col-span-2">
              <Textarea id="po-notes" rows={2} maxLength={1000} value={notes} onChange={(e) => setNotes(e.target.value)} />
            </Field>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("lines")}</CardTitle>
            <CardDescription>{t("linesHint")}</CardDescription>
          </CardHeader>
          <CardContent className="space-y-5">
            <VariantPicker chosen={chosen} idPrefix="po" onAdd={addVariant} />
            {rows.length === 0 ? (
              <p className="text-sm text-muted">{t("noLinesYet")}</p>
            ) : (
              <ul className="divide-y divide-line rounded-control border border-line" aria-label={t("lines")} data-testid="po-lines">
                {rows.map((row) => (
                  <LineRow key={row.variantId} row={row} onChange={change} onRemove={remove} />
                ))}
              </ul>
            )}
            {errors.lines ? (
              <p role="alert" className="text-sm font-medium text-danger">
                {errors.lines}
              </p>
            ) : null}
            <div className="flex items-center justify-between gap-3 border-t border-line pt-3">
              <span className="font-semibold text-strong">{t("total")}</span>
              <span className="text-xl font-semibold tabular-nums text-strong" data-testid="po-draft-total">
                {money(total)}
              </span>
            </div>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("attachments")}</CardTitle>
          </CardHeader>
          <CardContent>
            <AttachmentPicker idBase="po" saved={existing?.attachments ?? []} added={added} onAdded={setAdded} upload={(f) => uploadPurchaseFile(f)} check={receiptProblem} labels={attachLabels} onError={report} />
          </CardContent>
        </Card>

        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button variant="secondary" onClick={() => router.push(existing ? `/admin/purchasing/orders/${existing.id}` : "/admin/purchasing")}>
            {tc("cancel")}
          </Button>
          <Button type="submit" loading={save.isPending} data-testid="po-save">
            {t("saveDraft")}
          </Button>
        </div>
      </form>
    </>
  );
}

/** One line. Memoised and keyed by variant, so typing in one line re-renders only that line (and the total above). */
const LineRow = React.memo(function LineRow({ row, onChange, onRemove }: { row: Row; onChange: (variantId: string, patch: Partial<DraftLine>) => void; onRemove: (variantId: string) => void }) {
  const t = useTranslations("purchasing.editor");
  const money = useMoney2();
  const q = parseQuantity(row.quantity);
  const c = parseAmount(row.unitCost);
  const id = row.variantId;
  return (
    <li className="flex flex-col gap-3 p-3 lg:flex-row lg:items-end lg:justify-between" data-testid="po-line" data-sku={row.sku}>
      <div className="min-w-0 lg:flex-1">
        <p className="font-semibold text-strong [overflow-wrap:anywhere]">{row.productName}</p>
        <VariantText sku={row.sku} colorName={row.colorName} size={row.size} />
      </div>
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-28">
          <label htmlFor={`po-qty-${id}`} className="mb-1 block text-sm font-semibold text-strong">
            {t("quantity")}
          </label>
          <Input id={`po-qty-${id}`} inputMode="numeric" autoComplete="off" value={row.quantity} onChange={(e) => onChange(id, { quantity: e.target.value })} />
        </div>
        <div className="w-36">
          <label htmlFor={`po-cost-${id}`} className="mb-1 block text-sm font-semibold text-strong">
            {t("unitCost")}
          </label>
          <Input id={`po-cost-${id}`} inputMode="decimal" autoComplete="off" value={row.unitCost} onChange={(e) => onChange(id, { unitCost: e.target.value })} />
        </div>
        <p className="min-w-28 pb-2.5 text-end tabular-nums text-strong" aria-label={t("lineTotal")}>
          {q !== null && c !== null ? money(lineTotal(q, c)) : "-"}
        </p>
        <Button variant="ghost" size="icon" aria-label={t("remove", { sku: row.sku })} onClick={() => onRemove(id)}>
          <Trash2 aria-hidden />
        </Button>
      </div>
    </li>
  );
});
