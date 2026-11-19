"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleAlert, CircleCheck, FileUp } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber } from "@/modules/catalog/format";
import { useDistricts, useImportUpazilas } from "./api";
import { MAX_IMPORT, importSummary, importToRequest, parseUpazilaImport } from "./logic";

const SHOWN = 60;

type Props = { open: boolean; onOpenChange: (open: boolean) => void };

/**
 * Adds upazilas the loaded dataset lacks (FR-G02). The text is checked here first (district found, name present, no repeats) and the
 * rows that pass are sent in one request. The API has no dry run: names that already exist in a district are skipped by the server,
 * and the result says how many were added and skipped.
 */
export default function UpazilaImport({ open, onOpenChange }: Props) {
  const t = useTranslations("delivery.import");
  const tc = useTranslations("delivery.common");
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{t("description", { max: MAX_IMPORT })}</DialogDescription>
        {open ? <Body /> : null}
      </ModalContent>
    </Dialog>
  );
}

function Body() {
  const t = useTranslations("delivery.import");
  const te = useTranslations("delivery.import.errors");
  const tc = useTranslations("delivery.common");
  const problemMessage = useProblemMessage();
  const districts = useDistricts();
  const run = useImportUpazilas();
  const [text, setText] = React.useState("");
  const [result, setResult] = React.useState<{ added: number; skipped: number } | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);
  const fileRef = React.useRef<HTMLInputElement>(null);

  const rows = React.useMemo(() => (districts.data ? parseUpazilaImport(text, districts.data) : []), [text, districts.data]);
  const summary = importSummary(rows);
  const bad = rows.filter((r) => r.error);
  const canSend = summary.valid > 0 && !summary.tooMany && !run.isPending;

  const readFile = async (file: File | undefined) => {
    if (!file) return;
    setText(await file.text());
    setResult(null);
  };

  const send = async () => {
    setFailure(null);
    try {
      const r = await run.mutateAsync(importToRequest(rows));
      setResult({ added: toNumber(r.added), skipped: toNumber(r.skipped) });
      toast.success(t("done", { added: toNumber(r.added), skipped: toNumber(r.skipped) }));
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <div className="mt-4 space-y-4">
      <Alert variant="info">{t("format")}</Alert>
      <Field label={t("text")} htmlFor="imp-text" hint={t("textHint")}>
        <Textarea
          id="imp-text"
          rows={8}
          value={text}
          onChange={(e) => {
            setText(e.target.value);
            setResult(null);
          }}
          className="font-mono text-base"
          spellCheck={false}
          placeholder={t("placeholder")}
        />
      </Field>
      <div>
        <input ref={fileRef} type="file" accept=".csv,.txt,text/csv,text/plain" className="sr-only" tabIndex={-1} aria-hidden onChange={(e) => void readFile(e.target.files?.[0])} />
        <Button type="button" variant="secondary" onClick={() => fileRef.current?.click()}>
          <FileUp aria-hidden /> {t("chooseFile")}
        </Button>
      </div>

      {rows.length > 0 ? (
        <div className="space-y-2" data-testid="import-preview">
          <p className="flex flex-wrap items-center gap-2 font-medium text-strong" data-testid="import-summary">
            <Badge variant="success">
              <CircleCheck className="size-3.5" aria-hidden /> {t("valid", { count: summary.valid })}
            </Badge>
            {summary.invalid > 0 ? (
              <Badge variant="danger">
                <CircleAlert className="size-3.5" aria-hidden /> {t("invalid", { count: summary.invalid })}
              </Badge>
            ) : null}
          </p>
          {summary.tooMany ? <Alert variant="danger">{t("tooMany", { max: MAX_IMPORT })}</Alert> : null}
          {bad.length > 0 ? (
            <ul className="space-y-1 rounded-card border border-line p-3 text-sm" data-testid="import-errors">
              {bad.slice(0, SHOWN).map((r) => (
                <li key={r.line} className="[overflow-wrap:anywhere]">
                  <span className="font-semibold text-danger">{t("line", { line: r.line })}</span> {te(r.error!)}{" "}
                  <span className="font-mono text-xs text-muted">{[r.districtText, r.nameEn].filter(Boolean).join(", ")}</span>
                </li>
              ))}
              {bad.length > SHOWN ? <li className="text-muted">{t("moreErrors", { count: bad.length - SHOWN })}</li> : null}
            </ul>
          ) : null}
        </div>
      ) : null}

      {result ? (
        <Alert variant="success" data-testid="import-result">
          {t("done", { added: result.added, skipped: result.skipped })}
        </Alert>
      ) : null}
      {failure ? <Alert variant="danger">{failure}</Alert> : null}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={run.isPending}>
            {result ? tc("close") : tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="button" onClick={() => void send()} disabled={!canSend} loading={run.isPending} data-testid="import-send">
          {t("send", { count: summary.valid })}
        </Button>
      </div>
    </div>
  );
}
