"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Pencil, Plus, Ruler, Trash2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can } from "@/components/providers/session-provider";
import { useCategoryTree, useCreateSizeChart, useReportError, useSizeCharts, useUpdateSizeChart, type CategoryNode, type SizeChart } from "./api";

export type SizeTable = { columns: string[]; rows: string[][] };

/** Reads the stored `{"columns":[...],"rows":[[...]]}`; anything unreadable becomes an empty table. */
export function parseSizeTable(json: string): SizeTable {
  try {
    const value = JSON.parse(json) as { columns?: unknown; rows?: unknown };
    const columns = Array.isArray(value.columns) ? value.columns.map(String) : [];
    const rows = Array.isArray(value.rows) ? value.rows.map((r) => (Array.isArray(r) ? r.map(String) : [])) : [];
    return { columns, rows };
  } catch {
    return { columns: [], rows: [] };
  }
}

const flattenCategories = (nodes: CategoryNode[], prefix = ""): { id: string; label: string }[] =>
  nodes.flatMap((n) => {
    const label = prefix ? `${prefix} › ${n.nameEn}` : n.nameEn;
    return [{ id: n.id, label }, ...flattenCategories(n.children, label)];
  });

type DialogState = { kind: "new" } | { kind: "edit"; chart: SizeChart } | null;

/** Size charts (the size guide customers see): a list and a dialog with a small editable table. */
export function SizeChartsManager() {
  const t = useTranslations("lookups.sizeCharts");
  const tc = useTranslations("lookups.common");
  const charts = useSizeCharts();
  const categories = useCategoryTree(true);
  const [dialog, setDialog] = React.useState<DialogState>(null);
  const categoryList = React.useMemo(() => flattenCategories(categories.data ?? []), [categories.data]);
  const categoryName = (id: string | null) => (id ? (categoryList.find((c) => c.id === id)?.label ?? "-") : t("anyCategory"));

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Can permission="catalog.size-chart.edit" scope="anywhere">
          <Button onClick={() => setDialog({ kind: "new" })}>
            <Plus aria-hidden /> {t("add")}
          </Button>
        </Can>
      </div>

      {charts.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {charts.isLoading ? (
        <div aria-busy="true" className="space-y-2">
          <Skeleton className="h-20" />
          <Skeleton className="h-20" />
        </div>
      ) : (charts.data ?? []).length === 0 && !charts.isError ? (
        <Card>
          <EmptyState icon={<Ruler />} title={t("emptyTitle")} description={t("emptyDescription")} />
        </Card>
      ) : (
        <ul className="grid gap-3 md:grid-cols-2" aria-label={t("columns.name")}>
          {(charts.data ?? []).map((chart) => {
            const table = parseSizeTable(chart.tableJson);
            return (
              <li key={chart.id}>
                <Card className="flex items-start gap-3 p-4">
                  <div className="min-w-0 flex-1">
                    <h3 className="font-display text-lg font-semibold text-strong [overflow-wrap:anywhere]">{chart.nameEn}</h3>
                    {chart.nameBn ? <p className="text-sm text-muted [overflow-wrap:anywhere]">{chart.nameBn}</p> : null}
                    <p className="mt-1 text-sm text-muted [overflow-wrap:anywhere]">{categoryName(chart.categoryId)}</p>
                    <p className="text-sm text-muted">{t("summary", { columns: table.columns.length, rows: table.rows.length })}</p>
                  </div>
                  <Can permission="catalog.size-chart.edit" scope="anywhere">
                    <Button variant="ghost" size="icon" aria-label={t("edit", { name: chart.nameEn })} onClick={() => setDialog({ kind: "edit", chart })}>
                      <Pencil aria-hidden />
                    </Button>
                  </Can>
                </Card>
              </li>
            );
          })}
        </ul>
      )}

      <Dialog open={dialog !== null} onOpenChange={(open) => !open && setDialog(null)}>
        <ModalContent size="lg" closeLabel={tc("close")}>
          {dialog ? <SizeChartForm key={dialog.kind === "edit" ? dialog.chart.id : "new"} chart={dialog.kind === "edit" ? dialog.chart : undefined} categories={categoryList} onDone={() => setDialog(null)} /> : null}
        </ModalContent>
      </Dialog>
    </div>
  );
}

function SizeChartForm({ chart, categories, onDone }: { chart?: SizeChart; categories: { id: string; label: string }[]; onDone: () => void }) {
  const t = useTranslations("lookups.sizeCharts");
  const tc = useTranslations("lookups.common");
  const report = useReportError();
  const create = useCreateSizeChart();
  const update = useUpdateSizeChart();
  const initial = React.useMemo<SizeTable>(() => (chart ? parseSizeTable(chart.tableJson) : { columns: [t("grid.defaultColumn", { index: 1 }), t("grid.defaultColumn", { index: 2 })], rows: [["", ""]] }), [chart, t]);
  const [nameEn, setNameEn] = React.useState(chart?.nameEn ?? "");
  const [nameBn, setNameBn] = React.useState(chart?.nameBn ?? "");
  const [categoryId, setCategoryId] = React.useState(chart?.categoryId ?? "");
  const [table, setTable] = React.useState<SizeTable>(initial);
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  const ANY = "__any";
  const setColumn = (index: number, value: string) => setTable((x) => ({ ...x, columns: x.columns.map((c, i) => (i === index ? value : c)) }));
  const setCell = (row: number, column: number, value: string) => setTable((x) => ({ ...x, rows: x.rows.map((r, i) => (i === row ? r.map((c, j) => (j === column ? value : c)) : r)) }));
  const addColumn = () => setTable((x) => ({ columns: [...x.columns, t("grid.defaultColumn", { index: x.columns.length + 1 })], rows: x.rows.map((r) => [...r, ""]) }));
  const removeColumn = (index: number) => setTable((x) => ({ columns: x.columns.filter((_, i) => i !== index), rows: x.rows.map((r) => r.filter((_, i) => i !== index)) }));
  const addRow = () => setTable((x) => ({ ...x, rows: [...x.rows, x.columns.map(() => "")] }));
  const removeRow = (index: number) => setTable((x) => ({ ...x, rows: x.rows.filter((_, i) => i !== index) }));

  const validate = (): Record<string, string> => {
    const found: Record<string, string> = {};
    if (!nameEn.trim()) found.nameEn = tc("nameRequired");
    if (table.columns.length === 0) found.tableJson = t("grid.needColumn");
    else if (table.columns.some((c) => !c.trim())) found.tableJson = t("grid.titleRequired");
    else if (table.rows.length === 0) found.tableJson = t("grid.needRow");
    else if (table.rows.some((r) => r.some((c) => !c.trim()))) found.tableJson = t("grid.cellRequired");
    return found;
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validate();
    setErrors(found);
    if (Object.keys(found).length > 0) return;
    const body = {
      nameEn: nameEn.trim(),
      nameBn: nameBn.trim() || null,
      categoryId: categoryId || null,
      tableJson: JSON.stringify({ columns: table.columns.map((c) => c.trim()), rows: table.rows.map((r) => r.map((c) => c.trim())) }),
    };
    try {
      if (chart) await update.mutateAsync({ id: chart.id, body });
      else await create.mutateAsync(body);
      toast.success(chart ? tc("saved") : tc("created"));
      onDone();
    } catch (error) {
      setErrors(report(error));
    }
  };

  return (
    <form onSubmit={submit} noValidate className="space-y-5">
      <div>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{chart ? t("dialogEditTitle") : t("dialogNewTitle")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("dialogDescription")}</DialogDescription>
      </div>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={tc("nameEn")} htmlFor="sc-name-en" error={errors.nameEn} required>
          <Input id="sc-name-en" value={nameEn} onChange={(e) => setNameEn(e.target.value)} autoComplete="off" aria-invalid={!!errors.nameEn} />
        </Field>
        <Field label={tc("nameBn")} htmlFor="sc-name-bn" error={errors.nameBn}>
          <Input id="sc-name-bn" value={nameBn} onChange={(e) => setNameBn(e.target.value)} autoComplete="off" lang="bn" />
        </Field>
        <Field label={t("category")} htmlFor="sc-category" error={errors.categoryId} className="sm:col-span-2">
          <Select
            id="sc-category"
            value={categoryId || ANY}
            onValueChange={(v) => setCategoryId(v === ANY ? "" : v)}
            options={[{ value: ANY, label: t("anyCategory") }, ...categories.map((c) => ({ value: c.id, label: c.label }))]}
            placeholder={t("anyCategory")}
            searchPlaceholder={t("searchCategory")}
            emptyText={tc("search")}
            searchable
          />
        </Field>
      </div>

      <fieldset className="space-y-2">
        <legend className="font-semibold text-strong">{t("grid.label")}</legend>
        <p className="text-sm text-muted">{t("grid.hint")}</p>
        {/* Only this box scrolls sideways on a narrow screen, never the page. It is focusable so the keyboard can scroll it. */}
        <div role="region" aria-label={t("grid.label")} tabIndex={0} className="scroll-thin overflow-x-auto rounded-control border border-line">
          <table className="w-max min-w-full border-collapse text-start">
            <thead>
              <tr className="bg-sunken align-top">
                {table.columns.map((column, c) => (
                  <th key={c} scope="col" className="min-w-36 p-2 text-start">
                    <div className="flex items-center gap-1">
                      <Input aria-label={t("grid.columnTitle", { index: c + 1 })} value={column} onChange={(e) => setColumn(c, e.target.value)} autoComplete="off" className="min-w-0 font-semibold" />
                      <Button type="button" variant="ghost" size="icon" aria-label={t("grid.removeColumn", { index: c + 1 })} disabled={table.columns.length <= 1} onClick={() => removeColumn(c)}>
                        <X aria-hidden />
                      </Button>
                    </div>
                  </th>
                ))}
                <th className="w-px p-2">
                  <span className="sr-only">{t("columns.actions")}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {table.rows.map((row, r) => (
                <tr key={r} className="border-t border-line">
                  {row.map((cell, c) => (
                    <td key={c} className="p-2">
                      <Input aria-label={t("grid.cell", { row: r + 1, column: table.columns[c] || c + 1 })} value={cell} onChange={(e) => setCell(r, c, e.target.value)} autoComplete="off" className="min-w-0" />
                    </td>
                  ))}
                  <td className="p-2">
                    <Button type="button" variant="ghost" size="icon" aria-label={t("grid.removeRow", { index: r + 1 })} onClick={() => removeRow(r)}>
                      <Trash2 aria-hidden />
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {errors.tableJson ? (
          <p role="alert" className="text-sm font-medium text-danger">
            {errors.tableJson}
          </p>
        ) : null}
        <div className="flex flex-wrap gap-2">
          <Button type="button" variant="secondary" onClick={addColumn}>
            <Plus aria-hidden /> {t("grid.addColumn")}
          </Button>
          <Button type="button" variant="secondary" onClick={addRow}>
            <Plus aria-hidden /> {t("grid.addRow")}
          </Button>
        </div>
      </fieldset>

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
