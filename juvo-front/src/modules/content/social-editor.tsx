"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Alert, Badge, Card, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useDeleteSocial, useSaveSocial, useSocial, type SocialLink } from "./api";
import { FailureAlert, ReadOnlyNote, useContentProblem, useContentRights } from "./common";
import { nextSortOrder, socialRows, validateSocialInput, type Platform } from "./logic";

/** The five social platforms: switch on or off, set the address (or the WhatsApp number), remove. */
export function SocialEditor() {
  const t = useTranslations("content.social");
  const rights = useContentRights();
  const problem = useContentProblem();
  const social = useSocial();
  if (social.isPending) return <Skeleton className="h-96 w-full" aria-busy="true" />;
  if (social.isError) return <Alert variant="danger">{problem(social.error instanceof ApiError ? social.error.problem : null).text}</Alert>;
  const rows = socialRows(social.data);
  return (
    <div className="space-y-4">
      <p className="max-w-3xl text-muted">{t("subtitle")}</p>
      {!rights.socialEdit ? <ReadOnlyNote testId="social-read-only">{t("readOnly")}</ReadOnlyNote> : null}
      <ul className="grid gap-3" data-testid="social-list">
        {rows.map((row) => (
          <li key={row.platform}>
            <SocialRow
              // Remount when the saved copy changes so the fields start from what is saved.
              key={`${row.platform}-${row.saved?.updatedAt ?? "new"}`}
              platform={row.platform}
              saved={row.saved}
              nextOrder={nextSortOrder(social.data)}
              canEdit={rights.socialEdit}
            />
          </li>
        ))}
      </ul>
    </div>
  );
}

function SocialRow({ platform, saved, nextOrder, canEdit }: { platform: Platform; saved: SocialLink | null; nextOrder: number; canEdit: boolean }) {
  const t = useTranslations("content.social");
  const tp = useTranslations("content.platform");
  const tc = useTranslations("content.common");
  const problem = useContentProblem();
  const save = useSaveSocial();
  const remove = useDeleteSocial();
  const [url, setUrl] = React.useState(saved?.url ?? "");
  const [enabled, setEnabled] = React.useState(saved?.isEnabled ?? true);
  const [error, setError] = React.useState<string | null>(null);
  const [failure, setFailure] = React.useState<{ text: string; lines: string[] } | null>(null);
  const [confirmRemove, setConfirmRemove] = React.useState(false);
  const dirty = url !== (saved?.url ?? "") || enabled !== (saved?.isEnabled ?? true);
  const id = `so-${platform.toLowerCase()}`;
  const isWhatsApp = platform === "WhatsApp";

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const code = validateSocialInput(platform, url);
    setError(code ? t(`errors.${code}`) : null);
    if (code) return;
    try {
      await save.mutateAsync({ platform, url: url.trim(), isEnabled: enabled, sortOrder: saved ? Number(saved.sortOrder) : nextOrder });
      toast.success(t("saved", { platform: tp(platform) }));
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const doRemove = async () => {
    try {
      await remove.mutateAsync(platform);
      toast.success(t("removed", { platform: tp(platform) }));
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null));
    } finally {
      setConfirmRemove(false);
    }
  };

  return (
    <Card className="p-4" data-testid={`social-row-${platform.toLowerCase()}`}>
      <form onSubmit={(e) => void submit(e)} noValidate className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="font-sans text-base font-semibold text-strong">{tp(platform)}</h2>
          {saved ? (
            <Badge variant={saved.isEnabled ? "success" : "outline"} data-testid="social-state">
              {saved.isEnabled ? t("shown") : t("hidden")}
            </Badge>
          ) : (
            <Badge variant="outline">{t("notSet")}</Badge>
          )}
        </div>
        <Field label={isWhatsApp ? t("urlOrNumber") : t("url")} htmlFor={`${id}-url`} hint={isWhatsApp ? t("whatsAppHint") : t("urlHint")} error={error ?? undefined}>
          <Input id={`${id}-url`} type="text" inputMode={isWhatsApp ? "text" : "url"} value={url} disabled={!canEdit} onChange={(e) => setUrl(e.target.value)} autoComplete="off" autoCapitalize="off" spellCheck={false} aria-invalid={!!error} />
        </Field>
        <ChoiceRow htmlFor={`${id}-on`}>
          <Switch id={`${id}-on`} checked={enabled} disabled={!canEdit} onCheckedChange={setEnabled} />
          {t("showOnShop")}
        </ChoiceRow>
        <FailureAlert failure={failure} testId={`${id}-error`} />
        {canEdit ? (
          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            {saved ? (
              <Button type="button" variant="secondary" disabled={remove.isPending || save.isPending} onClick={() => setConfirmRemove(true)} data-testid={`${id}-remove`}>
                <Trash2 className="size-4" aria-hidden />
                {t("remove")}
                <span className="sr-only"> ({tp(platform)})</span>
              </Button>
            ) : null}
            <Button type="submit" loading={save.isPending} disabled={!dirty} data-testid={`${id}-save`}>
              {t("save")}
              <span className="sr-only"> ({tp(platform)})</span>
            </Button>
          </div>
        ) : null}
      </form>
      <ConfirmDialog
        open={confirmRemove}
        onOpenChange={setConfirmRemove}
        title={t("removeTitle", { platform: tp(platform) })}
        description={t("removeBody")}
        confirmLabel={t("remove")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={() => void doRemove()}
      />
    </Card>
  );
}
