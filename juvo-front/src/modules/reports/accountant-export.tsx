"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Download } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { downloadFile } from "./api";
import { RangeAlert } from "./common";
import { ACCOUNTANT_KINDS, MAX_DAYS_DAILY, accountantUrl, costKind, kindUsesRange, rangeProblem, type AccountantKind, type ReportFilters } from "./logic";

/**
 * The accountant export (FR-X05): a file for the accountant, CSV or Excel, of the day book, approved expenses, profit and loss, open
 * payables or received purchases, for the range and branch chosen above. Profit and loss and purchases need cost access: the API's
 * refusal is shown as it is. At most 366 days.
 */
export function AccountantExport({ filters }: { filters: ReportFilters }) {
  const t = useTranslations("reports.accountant");
  const tc = useTranslations("accounting.common");
  const message = useProblemMessage();
  const [kind, setKind] = React.useState<AccountantKind>("daybook");
  const [format, setFormat] = React.useState<"csv" | "xlsx">("csv");
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [done, setDone] = React.useState<string | null>(null);
  const problem = kindUsesRange(kind) ? rangeProblem(filters.from, filters.to, "day", MAX_DAYS_DAILY) : null;

  const run = async () => {
    setBusy(true);
    setError(null);
    setDone(null);
    try {
      const saved = await downloadFile(accountantUrl(kind, format, filters), `${kind}.${format}`);
      setDone(saved.name);
    } catch (e) {
      setError(message(e instanceof ApiError ? e.problem : undefined));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-4" data-testid="accountant-export">
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t("kind")} htmlFor="ax-kind" hint={costKind(kind) ? t("needsCost") : kindUsesRange(kind) ? undefined : t("noRange")}>
          <Select id="ax-kind" value={kind} onValueChange={(v) => setKind(v as AccountantKind)} options={ACCOUNTANT_KINDS.map((k) => ({ value: k, label: t(`kinds.${k}`) }))} placeholder={t("kind")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
        <Field label={t("format")} htmlFor="ax-format">
          <Select id="ax-format" value={format} onValueChange={(v) => setFormat(v === "xlsx" ? "xlsx" : "csv")} options={[{ value: "csv", label: t("formats.csv") }, { value: "xlsx", label: t("formats.xlsx") }]} placeholder={t("format")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
      </div>
      <p className="text-sm text-muted">{t(`kindHelp.${kind}`)}</p>
      {problem ? <RangeAlert problem={problem} groupBy="day" max={MAX_DAYS_DAILY} /> : null}
      {error ? (
        <Alert variant="danger" data-testid="accountant-error">
          {error}
        </Alert>
      ) : null}
      {done ? (
        <Alert variant="success" data-testid="accountant-done">
          {t("saved", { name: done })}
        </Alert>
      ) : null}
      <Button onClick={() => void run()} loading={busy} disabled={!!problem} data-testid="accountant-download">
        <Download aria-hidden /> {t("download")}
      </Button>
    </div>
  );
}
