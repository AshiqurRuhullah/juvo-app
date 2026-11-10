"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { CircleAlert, Download, FileDown, FileUp, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Input, Label, RadioGroup, RadioItem } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, DialogTrigger, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { Can } from "@/components/providers/session-provider";
import { useTableLabels } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { exportUrl, templateUrl, useImportProducts, type ImportIssue, type ImportReport, type ProductStatus } from "./api";
import { toNumber } from "./format";

type Mode = "create" | "upsert";
type Filters = { status?: string; categoryId?: string };
type Row = ImportIssue & { key: number };

/**
 * Template download, export and import for the products list (FR-B09). The import first runs as a check (nothing is saved) and shows
 * every problem with its row and column; only a file without errors can then be imported for real.
 */
export function ImportExportButtons({ filters }: { filters?: Filters }) {
  const t = useTranslations("lookups.importExport");
  const exportFilters = { status: (filters?.status || undefined) as ProductStatus | undefined, categoryId: filters?.categoryId || undefined };

  return (
    <div className="flex flex-wrap items-center gap-2">
      <Can permission="catalog.product.import" scope="anywhere">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="secondary">
              <FileDown aria-hidden /> {t("template")}
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent>
            <DropdownMenuItem asChild>
              <a href={templateUrl("csv")} download>
                {t("templateCsv")}
              </a>
            </DropdownMenuItem>
            <DropdownMenuItem asChild>
              <a href={templateUrl("xlsx")} download>
                {t("templateXlsx")}
              </a>
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </Can>
      <Can permission="catalog.product.export" scope="anywhere">
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="secondary">
              <Download aria-hidden /> {t("export")}
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent>
            <DropdownMenuItem asChild>
              <a href={exportUrl("csv", exportFilters)} download>
                {t("exportCsv")}
              </a>
            </DropdownMenuItem>
            <DropdownMenuItem asChild>
              <a href={exportUrl("xlsx", exportFilters)} download>
                {t("exportXlsx")}
              </a>
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </Can>
      <Can permission="catalog.product.import" scope="anywhere">
        <ImportDialog />
      </Can>
    </div>
  );
}

function ImportDialog() {
  const t = useTranslations("lookups.importExport");
  const tc = useTranslations("lookups.common");
  const problemMessage = useProblemMessage();
  const importer = useImportProducts();
  const inputRef = React.useRef<HTMLInputElement>(null);

  const [open, setOpen] = React.useState(false);
  const [file, setFile] = React.useState<File | null>(null);
  const [mode, setMode] = React.useState<Mode>("create");
  const [report, setReport] = React.useState<ImportReport | null>(null);
  const [refused, setRefused] = React.useState<string | null>(null);

  const reset = () => {
    setFile(null);
    setReport(null);
    setRefused(null);
    setMode("create");
    importer.reset();
  };

  const run = async (dryRun: boolean) => {
    if (!file) return;
    setRefused(null);
    try {
      const result = await importer.mutateAsync({ file, mode, dryRun });
      setReport(result);
      if (!dryRun && result.committed) toast.success(t("doneOk"));
    } catch (error) {
      setReport(null);
      const problem = error instanceof ApiError ? error.problem : null;
      setRefused(problem ? (fieldErrors(problem).file ?? problemMessage(problem)) : problemMessage(null));
    }
  };

  const done = report?.committed === true;
  const hasErrors = !!report && toNumber(report.errorCount) > 0;
  const checked = !!report && report.dryRun && !hasErrors;

  return (
    <Dialog
      open={open}
      onOpenChange={(next) => {
        setOpen(next);
        if (!next) reset();
      }}
    >
      <DialogTrigger asChild>
        <Button>
          <FileUp aria-hidden /> {t("import")}
        </Button>
      </DialogTrigger>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <div className="space-y-5">
          <div>
            <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
            <DialogDescription className="mt-1 text-muted">{t("description")}</DialogDescription>
          </div>

          {!done ? (
            <>
              <div className="space-y-1.5">
                <Label htmlFor="import-file">{t("file")}</Label>
                <Input
                  ref={inputRef}
                  id="import-file"
                  type="file"
                  accept=".csv,.xlsx,text/csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                  className="h-auto min-h-12 py-2.5 file:me-3 file:rounded-control file:border-0 file:bg-primary-soft file:px-3 file:py-1.5 file:font-semibold file:text-primary-dark"
                  onChange={(e) => {
                    setFile(e.target.files?.[0] ?? null);
                    setReport(null);
                    setRefused(null);
                  }}
                />
                <p className="text-sm text-muted">{file ? t("chosen", { name: file.name }) : t("fileHint")}</p>
              </div>

              <fieldset className="space-y-2">
                <legend className="font-semibold text-strong">{t("mode")}</legend>
                <RadioGroup value={mode} onValueChange={(v) => { setMode(v as Mode); setReport(null); }} aria-label={t("mode")}>
                  {(["create", "upsert"] as const).map((value) => (
                    <label key={value} htmlFor={`import-mode-${value}`} className="flex min-h-11 cursor-pointer items-start gap-3 py-1.5">
                      <RadioItem id={`import-mode-${value}`} value={value} className="mt-0.5" />
                      <span>
                        <span className="block font-medium text-strong">{value === "create" ? t("modeCreate") : t("modeUpsert")}</span>
                        <span className="block text-sm text-muted">{value === "create" ? t("modeCreateHint") : t("modeUpsertHint")}</span>
                      </span>
                    </label>
                  ))}
                </RadioGroup>
              </fieldset>
            </>
          ) : null}

          {refused ? <Alert variant="danger">{`${t("refused")} ${refused}`}</Alert> : null}

          {report ? <ReportView report={report} /> : null}
          {importer.isPending ? <p role="status" className="text-muted">{importer.variables?.dryRun ? t("checking") : t("importing")}</p> : null}

          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            {done ? (
              <Button onClick={() => setOpen(false)}>{t("close")}</Button>
            ) : (
              <>
                {report ? (
                  <Button variant="secondary" onClick={() => { setFile(null); setReport(null); if (inputRef.current) inputRef.current.value = ""; }}>
                    {t("again")}
                  </Button>
                ) : null}
                <Button variant={checked ? "secondary" : "primary"} disabled={!file} loading={importer.isPending && importer.variables?.dryRun === true} onClick={() => void run(true)}>
                  {t("check")}
                </Button>
                {checked ? (
                  <Button loading={importer.isPending && importer.variables?.dryRun === false} onClick={() => void run(false)}>
                    {t("importNow")}
                  </Button>
                ) : null}
              </>
            )}
          </div>
        </div>
      </ModalContent>
    </Dialog>
  );
}

function ReportView({ report }: { report: ImportReport }) {
  const t = useTranslations("lookups.importExport");
  const labels = useTableLabels();
  const errors = toNumber(report.errorCount);
  const [page, setPage] = React.useState(1);
  const pageSize = 15;
  const issues = report.issues;
  const slice = React.useMemo(() => issues.map((issue, index) => ({ ...issue, key: index })).slice((page - 1) * pageSize, page * pageSize), [issues, page]);

  const columns = React.useMemo<ColumnDef<Row, unknown>[]>(
    () => [
      {
        id: "row",
        header: t("columns.row"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => <span className="font-semibold">{toNumber(row.original.row) === 0 ? t("fileLevel") : t("rowLabel", { row: toNumber(row.original.row) })}</span>,
      },
      { id: "column", header: t("columns.column"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <code className="text-sm [overflow-wrap:anywhere]">{row.original.column ?? "-"}</code> },
      {
        id: "severity",
        header: t("columns.severity"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.severity === "error" ? (
            <span className="inline-flex items-center gap-1.5 font-medium text-danger">
              <CircleAlert className="size-4" aria-hidden /> {t("error")}
            </span>
          ) : (
            <span className="inline-flex items-center gap-1.5 font-medium text-warning">
              <TriangleAlert className="size-4" aria-hidden /> {t("warning")}
            </span>
          ),
      },
      { id: "message", header: t("columns.message"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.message}</span> },
    ],
    [t],
  );

  const stats: [string, number][] = [
    [t("summary.rows"), toNumber(report.rowsRead)],
    [t("summary.productsCreated"), toNumber(report.productsCreated)],
    [t("summary.productsUpdated"), toNumber(report.productsUpdated)],
    [t("summary.variantsCreated"), toNumber(report.variantsCreated)],
    [t("summary.variantsUpdated"), toNumber(report.variantsUpdated)],
    [t("summary.stock"), toNumber(report.stockLinesApplied)],
    [t("summary.errors"), errors],
    [t("summary.warnings"), toNumber(report.warningCount)],
  ];

  return (
    <section aria-labelledby="import-report-title" className="space-y-4">
      <h3 id="import-report-title" className="font-display text-lg font-semibold text-strong">
        {report.committed ? t("doneTitle") : t("checkTitle")}
      </h3>
      {report.committed ? (
        <Alert variant="success">{t("doneOk")}</Alert>
      ) : errors > 0 ? (
        <Alert variant="danger">{`${t("rejected")} ${t("checkErrors")}`}</Alert>
      ) : (
        <Alert variant="success">{t("checkOk")}</Alert>
      )}
      <dl className="grid grid-cols-2 gap-2 sm:grid-cols-4">
        {stats.map(([label, value]) => (
          <div key={label} className="rounded-control border border-line bg-sunken/50 p-3">
            <dt className="text-sm text-muted">{label}</dt>
            <dd className="font-display text-xl font-semibold text-strong">{value}</dd>
          </div>
        ))}
      </dl>
      {issues.length > 0 ? (
        <div className="space-y-2">
          <h4 className="font-semibold text-strong">{t("issues")}</h4>
          {report.issuesTruncated ? <p className="text-sm text-muted">{t("issuesTruncated")}</p> : null}
          <DataTable
            caption={t("issues")}
            columns={columns}
            data={slice}
            labels={labels}
            page={page}
            pageSize={pageSize}
            total={issues.length}
            onPageChange={setPage}
            getRowId={(issue) => String(issue.key)}
          />
        </div>
      ) : (
        <p className="text-muted">{t("noIssues")}</p>
      )}
    </section>
  );
}
