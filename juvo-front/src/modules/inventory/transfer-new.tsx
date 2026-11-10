"use client";

import * as React from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useSession } from "@/components/providers/session-provider";
import { useReportError } from "@/modules/catalog/api";
import { useRequestTransfer } from "./api";
import { InventoryTabs, QtyInput, VariantPicker, VariantText, useBranchOptions, type PickedVariant } from "./common";

type Line = PickedVariant & { quantity: string };

/** Ask for stock to move between two branches: choose both branches and the variants with their quantities. */
export function TransferNew() {
  const t = useTranslations("inventory.newTransfer");
  const tn = useTranslations("nav");
  const tc = useTranslations("inventory.common");
  const router = useRouter();
  const report = useReportError();
  const { me } = useSession();
  const branches = useBranchOptions();
  const request = useRequestTransfer();

  const [from, setFrom] = React.useState<string>("");
  const [to, setTo] = React.useState<string>(me.activeBranchId ?? "");
  const [note, setNote] = React.useState("");
  const [lines, setLines] = React.useState<Line[]>([]);
  const [errors, setErrors] = React.useState<{ from?: string; to?: string; lines?: string }>({});

  const chosen = React.useMemo(() => new Set(lines.map((l) => l.variantId)), [lines]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: typeof errors = {};
    if (!from) next.from = t("errors.fromRequired");
    if (!to) next.to = t("errors.toRequired");
    if (from && to && from === to) next.to = t("errors.sameBranch");
    if (lines.length === 0) next.lines = t("errors.noLines");
    else if (lines.some((l) => !l.quantity || Number(l.quantity) < 1)) next.lines = t("errors.quantity");
    setErrors(next);
    if (Object.keys(next).length > 0) return;

    try {
      const created = await request.mutateAsync({ fromBranchId: from, toBranchId: to, note: note.trim() || null, lines: lines.map((l) => ({ variantId: l.variantId, quantity: Number(l.quantity) })) });
      toast.success(t("created"));
      router.push(`/admin/inventory/transfers/${created.id}`);
    } catch (e) {
      report(e);
    }
  };

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.inventory"), href: "/admin/inventory" }, { label: t("crumb"), href: "/admin/inventory/transfers" }, { label: t("title") }];

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={crumbs} />
      <InventoryTabs />
      <form onSubmit={submit} noValidate className="space-y-6">
        <Card>
          <CardHeader>
            <CardTitle>{t("branches")}</CardTitle>
            <CardDescription>{t("branchesHint")}</CardDescription>
          </CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            <Field label={t("from")} htmlFor="tr-from" error={errors.from} required>
              <Select id="tr-from" value={from} onValueChange={setFrom} options={branches} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.from} />
            </Field>
            <Field label={t("to")} htmlFor="tr-to" error={errors.to} required>
              <Select id="tr-to" value={to} onValueChange={setTo} options={branches} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} invalid={!!errors.to} />
            </Field>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>{t("lines")}</CardTitle>
            <CardDescription>{t("linesHint")}</CardDescription>
          </CardHeader>
          <CardContent className="space-y-5">
            <VariantPicker
              chosen={chosen}
              sourceBranchId={from || undefined}
              idPrefix="tr"
              onAdd={(v) => {
                setErrors((e) => ({ ...e, lines: undefined }));
                setLines((old) => (old.some((l) => l.variantId === v.variantId) ? old : [...old, { ...v, quantity: "1" }]));
              }}
            />
            {lines.length === 0 ? (
              <p className="text-sm text-muted">{t("noLinesYet")}</p>
            ) : (
              <ul className="divide-y divide-line rounded-control border border-line" aria-label={t("lines")}>
                {lines.map((line) => (
                  <li key={line.variantId} className="flex flex-col gap-3 p-3 sm:flex-row sm:items-center sm:justify-between">
                    <div className="min-w-0">
                      <p className="font-semibold text-strong [overflow-wrap:anywhere]">{line.productName}</p>
                      <VariantText sku={line.sku} colorName={line.colorName} size={line.size} />
                    </div>
                    <div className="flex items-end gap-2">
                      <div className="w-32">
                        <label htmlFor={`tr-qty-${line.variantId}`} className="mb-1 block text-sm font-semibold text-strong">
                          {t("quantity")}
                        </label>
                        <QtyInput
                          id={`tr-qty-${line.variantId}`}
                          value={line.quantity}
                          onValueChange={(q) => setLines((old) => old.map((l) => (l.variantId === line.variantId ? { ...l, quantity: q } : l)))}
                        />
                      </div>
                      <Button variant="ghost" size="icon" aria-label={t("remove", { sku: line.sku })} onClick={() => setLines((old) => old.filter((l) => l.variantId !== line.variantId))}>
                        <Trash2 aria-hidden />
                      </Button>
                    </div>
                  </li>
                ))}
              </ul>
            )}
            {errors.lines ? (
              <p role="alert" className="text-sm font-medium text-danger">
                {errors.lines}
              </p>
            ) : null}
          </CardContent>
        </Card>

        <Card>
          <CardContent className="pt-5 sm:pt-6">
            <Field label={t("note")} htmlFor="tr-note" hint={t("noteHint")}>
              <Textarea id="tr-note" value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} className="min-h-20" />
            </Field>
          </CardContent>
        </Card>

        {branches.length < 2 ? <Alert variant="info">{t("needTwoBranches")}</Alert> : null}

        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
          <Button variant="secondary" onClick={() => router.push("/admin/inventory/transfers")}>
            {tc("cancel")}
          </Button>
          <Button type="submit" loading={request.isPending}>
            {t("submit")}
          </Button>
        </div>
      </form>
    </>
  );
}
