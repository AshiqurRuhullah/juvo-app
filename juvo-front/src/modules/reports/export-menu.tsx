"use client";

import * as React from "react";
import { useLocale, useTranslations } from "next-intl";
import { Download, FileSpreadsheet, FileText, FileType } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/form";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { downloadFile, reportFallbackName } from "./api";
import { exportUrl, type ExportFormat, type ReportFilters, type ReportName } from "./logic";

const ICON = { csv: FileText, xlsx: FileSpreadsheet, pdf: FileType } as const;

/**
 * Export of one report as CSV, Excel or PDF. The file is fetched through the BFF and saved under the server's own file name.
 * CSV and Excel hold one table: where the report has several, the person picks the section. The PDF holds everything and has a language.
 */
export function ExportMenu({ report, filters, sections, disabled }: { report: ReportName; filters: ReportFilters; sections?: readonly string[]; disabled?: boolean }) {
  const t = useTranslations("reports.export");
  const ts = useTranslations("reports.sections");
  const tc = useTranslations("accounting.common");
  const message = useProblemMessage();
  const locale = useLocale();
  const [open, setOpen] = React.useState(false);
  const [format, setFormat] = React.useState<ExportFormat>("csv");
  const [lang, setLang] = React.useState<"en" | "bn">(locale === "bn" ? "bn" : "en");
  const [section, setSection] = React.useState<string>(sections?.[0] ?? "");
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [done, setDone] = React.useState<string | null>(null);

  const run = async () => {
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      const saved = await downloadFile(exportUrl(report, filters, { format, lang, section: sections ? section : undefined }), reportFallbackName(report, format));
      setDone(saved.name);
    } catch (e) {
      setError(message(e instanceof ApiError ? e.problem : undefined));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="secondary" disabled={disabled} data-testid="export-open">
          <Download aria-hidden /> {t("button")}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-[min(22rem,calc(100vw-2rem))] space-y-4" data-testid="export-panel">
        <fieldset className="space-y-2">
          <legend className="text-sm font-medium text-strong">{t("format")}</legend>
          <div className="grid grid-cols-3 gap-2">
            {(["csv", "xlsx", "pdf"] as const).map((f) => {
              const Icon = ICON[f];
              return (
                <Button key={f} variant={format === f ? "primary" : "secondary"} aria-pressed={format === f} data-testid={`export-format-${f}`} className="flex-col gap-1 px-2 py-2" onClick={() => setFormat(f)}>
                  <Icon aria-hidden />
                  <span className="text-sm">{t(`formats.${f}`)}</span>
                </Button>
              );
            })}
          </div>
        </fieldset>
        {format === "pdf" ? (
          <Field label={t("language")} htmlFor="ex-lang">
            <Select
              id="ex-lang"
              value={lang}
              onValueChange={(v) => setLang(v === "bn" ? "bn" : "en")}
              options={[
                { value: "en", label: t("languages.en") },
                { value: "bn", label: t("languages.bn") },
              ]}
              placeholder={t("language")}
              searchPlaceholder={tc("search")}
              emptyText={tc("noMatch")}
            />
          </Field>
        ) : sections && sections.length > 1 ? (
          <Field label={t("section")} htmlFor="ex-section" hint={t("sectionHint")}>
            <Select id="ex-section" value={section} onValueChange={setSection} options={sections.map((s) => ({ value: s, label: ts(`${report}.${s}`) }))} placeholder={t("section")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </Field>
        ) : null}
        <p className="text-sm text-muted">{format === "pdf" ? t("pdfHint") : t("tableHint")}</p>
        {error ? (
          <Alert variant="danger" data-testid="export-error">
            {error}
          </Alert>
        ) : null}
        {done ? (
          <Alert variant="success" data-testid="export-done">
            {t("saved", { name: done })}
          </Alert>
        ) : null}
        <Button className="w-full" loading={busy} onClick={() => void run()} data-testid="export-download">
          <Download aria-hidden /> {t("download")}
        </Button>
      </PopoverContent>
    </Popover>
  );
}
