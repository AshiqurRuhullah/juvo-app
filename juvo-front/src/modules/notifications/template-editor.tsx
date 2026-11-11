"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { History } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Switch, Textarea, Input } from "@/components/ui/form";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useDebounced } from "@/lib/hooks";
import { usePreviewTemplate, useSaveTemplate, useTemplateHistory, type Template } from "./api";
import { placeholderText, smsParts } from "./format";

/**
 * Edits one template (code, channel, language): text, active switch, placeholders to click in, a live preview with sample values
 * and the version history. Mount it only while its dialog is open so the form always starts from the template's current text.
 */
export function TemplateEditor({ template, canEdit, onDone }: { template: Template; canEdit: boolean; onDone: () => void }) {
  const t = useTranslations("notifications.editor");
  const dateTime = useDateTime();
  const problemText = useProblemMessage();
  const save = useSaveTemplate();
  const preview = usePreviewTemplate();
  const history = useTemplateHistory(template.code, template.channel, template.language);
  const isEmail = template.channel === "Email";

  const [subject, setSubject] = React.useState(template.subject ?? "");
  const [body, setBody] = React.useState(template.body);
  const [active, setActive] = React.useState(template.isActive);
  const [error, setError] = React.useState<string | null>(null);
  const [openVersion, setOpenVersion] = React.useState<number | null>(null);
  const area = React.useRef<HTMLTextAreaElement>(null);

  const debouncedBody = useDebounced(body, 400);
  const debouncedSubject = useDebounced(subject, 400);
  const { mutate: runPreview } = preview;
  React.useEffect(() => {
    runPreview({ code: template.code, channel: template.channel, language: template.language, subject: isEmail ? debouncedSubject : null, body: debouncedBody });
  }, [runPreview, template.code, template.channel, template.language, isEmail, debouncedSubject, debouncedBody]);

  const insert = (variable: string) => {
    const text = placeholderText(variable);
    const el = area.current;
    const start = el?.selectionStart ?? body.length;
    const end = el?.selectionEnd ?? body.length;
    setBody(body.slice(0, start) + text + body.slice(end));
    requestAnimationFrame(() => {
      el?.focus();
      el?.setSelectionRange(start + text.length, start + text.length);
    });
  };

  const submit = async () => {
    setError(null);
    try {
      await save.mutateAsync({ code: template.code, channel: template.channel, language: template.language, subject: isEmail ? subject.trim() || null : null, body, isActive: active });
      toast.success(t("saved"));
      onDone();
    } catch (failure) {
      setError(problemText(failure instanceof ApiError ? failure.problem : null));
    }
  };

  const shown = preview.data;
  const sms = template.channel !== "Email" ? smsParts(shown?.body ?? body) : null;
  const versions = history.data ?? [];

  return (
    <div className="space-y-5">
      {!canEdit ? <Alert variant="info">{t("readOnly")}</Alert> : null}

      <div className="grid gap-5 lg:grid-cols-2">
        <div className="min-w-0 space-y-4">
          {isEmail ? (
            <Field label={t("subject")} htmlFor="tpl-subject">
              <Input id="tpl-subject" value={subject} onChange={(e) => setSubject(e.target.value)} readOnly={!canEdit} />
            </Field>
          ) : null}
          <Field label={t("body")} htmlFor="tpl-body" hint={t("bodyHint")}>
            <Textarea id="tpl-body" ref={area} rows={isEmail ? 10 : 6} value={body} onChange={(e) => setBody(e.target.value)} readOnly={!canEdit} className="text-base" />
          </Field>

          <div>
            <p className="mb-1 text-sm font-semibold text-strong" id="tpl-vars">
              {t("placeholders")}
            </p>
            <p className="mb-2 text-sm text-muted">{canEdit ? t("placeholdersHelp") : t("placeholdersRead")}</p>
            <ul className="flex flex-wrap gap-2" aria-labelledby="tpl-vars">
              {template.variables.map((v) => (
                <li key={v}>
                  <Button type="button" variant="secondary" disabled={!canEdit} onClick={() => insert(v)} className="font-mono text-sm" aria-label={t("insert", { name: placeholderText(v) })}>
                    {placeholderText(v)}
                  </Button>
                </li>
              ))}
            </ul>
          </div>

          <ChoiceRow htmlFor="tpl-active">
            <Switch id="tpl-active" checked={active} onCheckedChange={setActive} disabled={!canEdit} />
            <span>{t("active")}</span>
          </ChoiceRow>
          <p className="-mt-2 text-sm text-muted">{t("activeHelp")}</p>
        </div>

        <div className="min-w-0 space-y-3" aria-live="polite">
          <h3 className="text-base font-semibold text-strong">{t("preview")}</h3>
          <p className="text-sm text-muted">{t("previewHelp")}</p>
          <div className="space-y-2 rounded-control border border-line bg-sunken p-4" data-testid="template-preview">
            {isEmail && shown?.subject ? <p className="font-semibold text-strong [overflow-wrap:anywhere]">{shown.subject}</p> : null}
            <p className="whitespace-pre-wrap text-strong [overflow-wrap:anywhere]" data-testid="template-preview-body">
              {shown?.body ?? "..."}
            </p>
          </div>
          {shown ? (
            <p className="text-sm text-muted" data-testid="template-count">
              {t("characters", { count: Number(shown.characters) })}
              {sms && sms.parts > 0 ? ` · ${t("parts", { parts: sms.parts, limit: sms.limit })}` : ""}
            </p>
          ) : null}
          {shown && shown.unknownPlaceholders.length > 0 ? (
            <Alert variant="danger" title={t("unknown")}>
              <span className="font-mono">{shown.unknownPlaceholders.map(placeholderText).join(", ")}</span>
            </Alert>
          ) : null}
          {shown && shown.missingVariables.length > 0 ? (
            <Alert variant="warning" title={t("missing")}>
              <span className="font-mono">{shown.missingVariables.map(placeholderText).join(", ")}</span>
            </Alert>
          ) : null}
          {preview.isError ? <Alert variant="warning">{problemText(preview.error instanceof ApiError ? preview.error.problem : null)}</Alert> : null}
        </div>
      </div>

      <section aria-labelledby="tpl-history" className="space-y-2 border-t border-line pt-4">
        <h3 id="tpl-history" className="flex items-center gap-2 text-base font-semibold text-strong">
          <History className="size-5 text-primary" aria-hidden /> {t("history")}
        </h3>
        {history.isLoading ? <p className="text-sm text-muted">...</p> : null}
        <ol className="space-y-2" data-testid="template-history">
          {versions.map((v) => {
            const number = Number(v.version);
            const open = openVersion === number;
            return (
              <li key={number} className="rounded-control border border-line bg-card p-3" data-version={number}>
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-semibold text-strong">{number === 0 ? t("builtIn") : t("version", { n: number })}</span>
                  {v.isCurrent ? <Badge variant="success">{t("current")}</Badge> : null}
                  <span className="text-sm text-muted">{number === 0 ? "" : `${v.editedBy ?? ""} ${dateTime(v.editedAt)}`.trim()}</span>
                  <span className="flex-1" />
                  <Button type="button" variant="ghost" onClick={() => setOpenVersion(open ? null : number)} aria-expanded={open}>
                    {open ? t("hideText") : t("showText")}
                  </Button>
                  {canEdit && !v.isCurrent ? (
                    <Button
                      type="button"
                      variant="secondary"
                      onClick={() => {
                        setBody(v.body);
                        if (isEmail) setSubject(v.subject ?? "");
                        toast.success(t("copied"));
                      }}
                    >
                      {t("useText")}
                    </Button>
                  ) : null}
                </div>
                {open ? (
                  <div className="mt-2 space-y-1 rounded-control bg-sunken p-3 text-sm">
                    {v.subject ? <p className="font-semibold text-strong [overflow-wrap:anywhere]">{v.subject}</p> : null}
                    <p className="whitespace-pre-wrap font-mono text-strong [overflow-wrap:anywhere]">{v.body}</p>
                  </div>
                ) : null}
              </li>
            );
          })}
        </ol>
      </section>

      {error ? (
        <Alert variant="danger" data-testid="template-error">
          {error}
        </Alert>
      ) : null}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="secondary" onClick={onDone}>
          {canEdit ? t("cancel") : t("close")}
        </Button>
        {canEdit ? (
          <Button type="button" onClick={() => void submit()} loading={save.isPending} data-testid="template-save">
            {t("save")}
          </Button>
        ) : null}
      </div>
    </div>
  );
}
