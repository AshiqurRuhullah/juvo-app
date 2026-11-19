"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, EyeOff, Globe } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Input, Textarea } from "@/components/ui/form";
import { Tabs } from "@/components/ui/tabs";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { usePage, usePublishPage, useSavePage, type ContentPage } from "./api";
import { FailureAlert, PublishedBadge, ReadOnlyNote, useContentProblem, useContentRights } from "./common";
import { canPublishForm, hasErrors, pageIsDirty, pageLimit, pageToForm, publicPath, validatePage, type PageForm, type PageKey } from "./logic";
import { Markdown } from "./markdown-view";

/** Editor of one page: English and Bengali texts, meta fields, a live preview, save, publish and unpublish. */
export function PageEditor({ pageKey }: { pageKey: PageKey }) {
  const page = usePage(pageKey);
  const problem = useContentProblem();
  const t = useTranslations("content.editor");
  if (page.isPending) return <Skeleton className="h-96 w-full" aria-busy="true" />;
  if (page.isError) return <Alert variant="danger">{problem(page.error instanceof ApiError ? page.error.problem : null).text}</Alert>;
  // Remount when the saved copy changes, so the form is always initialised from what is saved (no effect needed).
  return <EditorBody key={`${page.data.updatedAt}-${page.data.isPublished}`} pageKey={pageKey} page={page.data} backLabel={t("back")} />;
}

function EditorBody({ pageKey, page, backLabel }: { pageKey: PageKey; page: ContentPage; backLabel: string }) {
  const t = useTranslations("content.editor");
  const tn = useTranslations("content.pageNames");
  const tc = useTranslations("content.common");
  const rights = useContentRights();
  const problem = useContentProblem();
  const save = useSavePage(pageKey);
  const publish = usePublishPage(pageKey);
  const saved = React.useMemo(() => pageToForm(page), [page]);
  const [form, setForm] = React.useState<PageForm>(saved);
  const [failure, setFailure] = React.useState<{ text: string; lines: string[] } | null>(null);
  const [confirm, setConfirm] = React.useState<"publish" | "unpublish" | null>(null);
  const [previewLang, setPreviewLang] = React.useState("en");
  const errors = validatePage(form);
  const dirty = pageIsDirty(form, saved);
  const canEdit = rights.pageEdit;
  const set = (key: keyof PageForm, value: string) => setForm((old) => ({ ...old, [key]: value }));
  const lengthError = (key: keyof PageForm) => (errors[key] ? t("tooLong", { max: pageLimit(key) }) : undefined);

  const doSave = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    if (hasErrors(errors)) return;
    try {
      await save.mutateAsync(form);
      toast.success(t("saved"));
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  const doPublish = async () => {
    const wantsPublish = confirm === "publish";
    setFailure(null);
    try {
      await publish.mutateAsync(wantsPublish);
      toast.success(wantsPublish ? t("published") : t("unpublished"));
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null));
    } finally {
      setConfirm(null);
    }
  };

  const name = tn(pageKey);
  const previewBody = previewLang === "bn" ? form.bodyBn : form.bodyEn;
  const previewTitle = previewLang === "bn" ? form.titleBn : form.titleEn;
  const fallbackNote = previewLang === "bn" && !form.bodyBn.trim();

  return (
    <form onSubmit={(e) => void doSave(e)} noValidate className="space-y-5" data-testid="page-editor">
      <div className="flex flex-wrap items-center gap-3">
        <Button asChild variant="ghost">
          <Link href="/admin/content/pages">
            <ArrowLeft className="size-4 rtl:rotate-180" aria-hidden />
            {backLabel}
          </Link>
        </Button>
        <PublishedBadge published={page.isPublished} />
        {page.isPublished ? (
          <a className="inline-flex min-h-11 items-center text-sm font-semibold text-primary hover:underline" href={publicPath(pageKey)} target="_blank" rel="noopener noreferrer" data-testid="page-public-link">
            {t("viewOnShop")}
          </a>
        ) : null}
      </div>
      {!canEdit ? <ReadOnlyNote testId="page-read-only">{t("readOnly")}</ReadOnlyNote> : null}

      <div className="grid gap-5 lg:grid-cols-2">
        {(["En", "Bn"] as const).map((side) => (
          <Card key={side}>
            <CardHeader>
              <CardTitle>{side === "En" ? t("english") : t("bengali")}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <Field label={t("title")} htmlFor={`pg-title-${side}`} required={side === "En"} error={lengthError(`title${side}`)}>
                <Input id={`pg-title-${side}`} value={form[`title${side}`]} disabled={!canEdit} onChange={(e) => set(`title${side}`, e.target.value)} autoComplete="off" lang={side === "Bn" ? "bn" : "en"} aria-invalid={!!errors[`title${side}`]} />
              </Field>
              <Field label={t("body")} htmlFor={`pg-body-${side}`} required={side === "En"} hint={side === "En" ? t("bodyHint") : t("bodyHintBn")} error={lengthError(`body${side}`)}>
                <Textarea id={`pg-body-${side}`} className="min-h-64 font-mono text-[0.9375rem]" value={form[`body${side}`]} disabled={!canEdit} onChange={(e) => set(`body${side}`, e.target.value)} lang={side === "Bn" ? "bn" : "en"} aria-invalid={!!errors[`body${side}`]} />
              </Field>
              <Field label={t("metaTitle")} htmlFor={`pg-mt-${side}`} hint={t("metaTitleHint")} error={lengthError(`metaTitle${side}`)}>
                <Input id={`pg-mt-${side}`} value={form[`metaTitle${side}`]} disabled={!canEdit} onChange={(e) => set(`metaTitle${side}`, e.target.value)} autoComplete="off" lang={side === "Bn" ? "bn" : "en"} aria-invalid={!!errors[`metaTitle${side}`]} />
              </Field>
              <Field label={t("metaDescription")} htmlFor={`pg-md-${side}`} hint={t("metaDescriptionHint")} error={lengthError(`metaDescription${side}`)}>
                <Textarea id={`pg-md-${side}`} className="min-h-24" value={form[`metaDescription${side}`]} disabled={!canEdit} onChange={(e) => set(`metaDescription${side}`, e.target.value)} lang={side === "Bn" ? "bn" : "en"} aria-invalid={!!errors[`metaDescription${side}`]} />
              </Field>
            </CardContent>
          </Card>
        ))}
      </div>

      <Card data-testid="page-preview">
        <CardHeader>
          <CardTitle>{t("preview")}</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3">
          <p className="text-sm text-muted">{t("previewHint")}</p>
          <Tabs
            label={t("previewLanguage")}
            tabs={[{ id: "en", label: t("english") }, { id: "bn", label: t("bengali") }]}
            value={previewLang}
            onValueChange={setPreviewLang}
          >
            {() => (
              <div className="rounded-card border border-line bg-sunken p-4 sm:p-6" lang={previewLang}>
                {fallbackNote ? <p className="mb-3 text-sm text-muted">{t("previewFallback")}</p> : null}
                {previewBody.trim() ? (
                  <>
                    {previewTitle.trim() ? <h2 className="mb-4 text-2xl font-semibold text-strong">{previewTitle}</h2> : null}
                    <Markdown source={previewBody} data-testid="page-preview-body" />
                  </>
                ) : (
                  <p className="text-muted">{t("previewEmpty")}</p>
                )}
              </div>
            )}
          </Tabs>
        </CardContent>
      </Card>

      <FailureAlert failure={failure} testId="page-error" />

      {canEdit || rights.pagePublish ? (
        <div className="flex flex-col-reverse gap-2 sm:flex-row sm:flex-wrap sm:items-center sm:justify-end">
          {dirty ? <Badge variant="warning">{tc("unsaved")}</Badge> : null}
          {canEdit && dirty ? (
            <Button type="button" variant="secondary" disabled={save.isPending} onClick={() => { setForm(saved); setFailure(null); }}>
              {tc("discard")}
            </Button>
          ) : null}
          {rights.pagePublish && page.isPublished ? (
            <Button type="button" variant="secondary" disabled={publish.isPending || dirty} onClick={() => setConfirm("unpublish")} data-testid="page-unpublish">
              <EyeOff className="size-4" aria-hidden />
              {t("unpublish")}
            </Button>
          ) : null}
          {rights.pagePublish && !page.isPublished ? (
            <Button type="button" variant="secondary" disabled={publish.isPending || dirty || !page.canPublish || !canPublishForm(saved)} onClick={() => setConfirm("publish")} data-testid="page-publish">
              <Globe className="size-4" aria-hidden />
              {t("publish")}
            </Button>
          ) : null}
          {canEdit ? (
            <Button type="submit" loading={save.isPending} disabled={!dirty} data-testid="page-save">
              {t("save")}
            </Button>
          ) : null}
        </div>
      ) : null}
      {rights.pagePublish && !page.isPublished && !page.canPublish ? <p className="text-end text-sm text-muted">{t("publishNeeds")}</p> : null}
      {rights.pagePublish && dirty && canEdit ? <p className="text-end text-sm text-muted">{t("saveBeforePublish")}</p> : null}

      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => (open ? undefined : setConfirm(null))}
        title={confirm === "unpublish" ? t("unpublishTitle", { name }) : t("publishTitle", { name })}
        description={confirm === "unpublish" ? t("unpublishBody", { path: publicPath(pageKey) }) : t("publishBody", { path: publicPath(pageKey) })}
        confirmLabel={confirm === "unpublish" ? t("unpublish") : t("publish")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={confirm === "unpublish"}
        loading={publish.isPending}
        onConfirm={() => void doPublish()}
      />
    </form>
  );
}
