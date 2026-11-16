"use client";

import * as React from "react";
import { useLocale, useTranslations } from "next-intl";
import { Field } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useCannedReplies, useRenderCanned } from "./api";

/**
 * "Insert a saved reply": language (the screen's language first), then the reply. The text is filled in by the server for this
 * ticket (name, order number, ticket number) and handed to `onInsert`. Loaded on demand, and the list is only fetched once opened.
 * Default export so it can be loaded with next/dynamic.
 */
export default function CannedPicker({ ticketId, onInsert }: { ticketId: string; onInsert: (text: string) => void }) {
  const t = useTranslations("support.picker");
  const tc = useTranslations("support.common");
  const locale = useLocale();
  const problemMessage = useProblemMessage();
  const [language, setLanguage] = React.useState(locale === "bn" ? "bn" : "en");
  const [note, setNote] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const list = useCannedReplies({ language });
  const render = useRenderCanned();

  const pick = async (id: string) => {
    setError(null);
    setNote(null);
    try {
      const out = await render.mutateAsync({ cannedReplyId: id, ticketId });
      onInsert(out.text);
      if (out.emptyPlaceholders.length > 0) setNote(t("empty", { names: out.emptyPlaceholders.map((n) => `{${n}}`).join(", ") }));
    } catch (e) {
      setError(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  const options = (list.data ?? []).map((r) => ({ value: r.id, label: r.title, hint: r.category ?? undefined }));

  return (
    <div className="space-y-3 rounded-control border border-line bg-sunken p-3" data-testid="canned-picker">
      <div className="grid gap-3 sm:grid-cols-[10rem_minmax(0,1fr)]">
        <Field label={t("language")} htmlFor="cp-language">
          <Select
            id="cp-language"
            value={language}
            onValueChange={setLanguage}
            options={[
              { value: "en", label: t("en") },
              { value: "bn", label: t("bn") },
            ]}
            placeholder={t("language")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
        </Field>
        <Field label={t("reply")} htmlFor="cp-reply" hint={list.isSuccess && options.length === 0 ? t("none") : undefined}>
          {list.isPending ? (
            <Skeleton className="h-12" aria-busy="true" />
          ) : (
            <Select id="cp-reply" value="" onValueChange={(v) => void pick(v)} options={options} placeholder={t("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} disabled={options.length === 0 || render.isPending} searchable={options.length > 7} />
          )}
        </Field>
      </div>
      {list.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {error ? <Alert variant="danger">{error}</Alert> : null}
      {note ? <Alert variant="warning">{note}</Alert> : null}
    </div>
  );
}
