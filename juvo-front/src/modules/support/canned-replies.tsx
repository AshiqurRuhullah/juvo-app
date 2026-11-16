"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Input, Switch, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useCannedReplies, useDeleteCanned, useSaveCanned, type CannedReply } from "./api";
import { useSupportPermissions } from "./common";
import { MESSAGE_MAX, PLACEHOLDERS, SAMPLE_VALUES, cannedProblem, renderPreview, unknownPlaceholders, type CannedForm } from "./logic";

const ANY = "__all";

/** Saved replies (FR-Y03): list by language, create, edit, delete, with placeholder help and a live preview. */
export default function CannedReplies() {
  const t = useTranslations("support.canned");
  const tc = useTranslations("support.common");
  const problemMessage = useProblemMessage();
  const perms = useSupportPermissions();
  const [language, setLanguage] = React.useState<string>(ANY);
  const [inactive, setInactive] = React.useState(true);
  const [editing, setEditing] = React.useState<CannedReply | "new" | null>(null);
  const [deleting, setDeleting] = React.useState<CannedReply | null>(null);
  const list = useCannedReplies({ language: language === ANY ? undefined : language, includeInactive: inactive });
  const remove = useDeleteCanned();
  const items = list.data ?? [];

  const doDelete = async () => {
    if (!deleting) return;
    try {
      await remove.mutateAsync(deleting.id);
      toast.success(t("deleted"));
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
    } finally {
      setDeleting(null);
    }
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <div className="w-full sm:w-56">
          <Field label={t("language")} htmlFor="cr-language">
            <Select
              id="cr-language"
              value={language}
              onValueChange={setLanguage}
              options={[
                { value: ANY, label: t("anyLanguage") },
                { value: "en", label: t("lang.en") },
                { value: "bn", label: t("lang.bn") },
              ]}
              placeholder={t("language")}
              searchPlaceholder={tc("search")}
              emptyText={tc("noMatch")}
            />
          </Field>
        </div>
        <label className="flex min-h-11 cursor-pointer items-center gap-3 text-base text-strong">
          <Switch checked={inactive} onCheckedChange={setInactive} aria-label={t("showInactive")} />
          <span>{t("showInactive")}</span>
        </label>
        {perms.cannedEdit ? (
          <Button className="ms-auto" onClick={() => setEditing("new")} data-testid="new-canned">
            <Plus aria-hidden /> {t("new")}
          </Button>
        ) : null}
      </div>

      {list.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {list.isPending ? (
        <div className="space-y-3" aria-busy="true">
          <Skeleton className="h-24" />
          <Skeleton className="h-24" />
        </div>
      ) : null}
      {list.isSuccess && items.length === 0 ? (
        <Card>
          <EmptyState title={t("empty")} description={t("emptyHelp")} />
        </Card>
      ) : null}

      <ul className="grid gap-3 lg:grid-cols-2" aria-label={t("title")}>
        {items.map((r) => (
          <li key={r.id}>
            <Card className="h-full p-4" data-testid="canned-item">
              <div className="flex flex-wrap items-center gap-2">
                <h3 className="font-semibold text-strong [overflow-wrap:anywhere]">{r.title}</h3>
                <Badge variant="outline">{t(`lang.${r.language === "bn" ? "bn" : "en"}`)}</Badge>
                {r.category ? <Badge variant="neutral">{r.category}</Badge> : null}
                {!r.isActive ? <Badge variant="warning">{t("inactive")}</Badge> : null}
              </div>
              <p className="mt-2 line-clamp-4 whitespace-pre-wrap text-sm text-muted [overflow-wrap:anywhere]">{r.body}</p>
              {perms.cannedEdit ? (
                <div className="mt-3 flex flex-wrap gap-2">
                  <Button variant="secondary" onClick={() => setEditing(r)} aria-label={t("editLabel", { title: r.title })}>
                    <Pencil aria-hidden /> {t("edit")}
                  </Button>
                  <Button variant="ghost" onClick={() => setDeleting(r)} aria-label={t("deleteLabel", { title: r.title })}>
                    <Trash2 aria-hidden /> {t("delete")}
                  </Button>
                </div>
              ) : null}
            </Card>
          </li>
        ))}
      </ul>

      {editing ? <EditDialog key={editing === "new" ? "new" : editing.id} reply={editing === "new" ? null : editing} onClose={() => setEditing(null)} /> : null}

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => {
          if (!open) setDeleting(null);
        }}
        title={t("deleteTitle")}
        description={t("deleteDescription", { title: deleting?.title ?? "" })}
        confirmLabel={t("delete")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={doDelete}
      />
    </div>
  );
}

function EditDialog({ reply, onClose }: { reply: CannedReply | null; onClose: () => void }) {
  const t = useTranslations("support.canned");
  const tc = useTranslations("support.common");
  const problemMessage = useProblemMessage();
  const save = useSaveCanned(reply?.id);
  const [form, setForm] = React.useState<CannedForm>({
    title: reply?.title ?? "",
    body: reply?.body ?? "",
    language: reply?.language === "bn" ? "bn" : "en",
    category: reply?.category ?? "",
    isActive: reply?.isActive ?? true,
  });
  const [errors, setErrors] = React.useState<{ title?: string; body?: string }>({});
  const [problem, setProblem] = React.useState<string | null>(null);
  const bodyRef = React.useRef<HTMLTextAreaElement>(null);
  const set = <K extends keyof CannedForm>(key: K, value: CannedForm[K]) => setForm((f) => ({ ...f, [key]: value }));

  const unknown = React.useMemo(() => unknownPlaceholders(form.body), [form.body]);
  const preview = React.useMemo(() => renderPreview(form.body, SAMPLE_VALUES).text, [form.body]);

  const insertPlaceholder = (name: string) => {
    const el = bodyRef.current;
    const token = `{${name}}`;
    const start = el?.selectionStart ?? form.body.length;
    const end = el?.selectionEnd ?? form.body.length;
    const next = form.body.slice(0, start) + token + form.body.slice(end);
    set("body", next);
    requestAnimationFrame(() => {
      el?.focus();
      el?.setSelectionRange(start + token.length, start + token.length);
    });
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setProblem(null);
    const found = cannedProblem(form);
    if (found) {
      setErrors({ [found.field]: t(`errors.${found.error}`, { max: MESSAGE_MAX, names: unknown.map((n) => `{${n}}`).join(", ") }) });
      return;
    }
    setErrors({});
    try {
      await save.mutateAsync(form);
      toast.success(reply ? t("saved") : t("created"));
      onClose();
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.code === "GENERAL.CONFLICT") setErrors({ title: t("errors.duplicate") });
      else if (p?.status === 400 && p.errors) {
        const fe = fieldErrors(p);
        setErrors({ title: fe.title, body: fe.body });
        if (!fe.title && !fe.body) setProblem(problemMessage(p));
      } else setProblem(problemMessage(p));
    }
  };

  return (
    <Dialog open onOpenChange={(open) => (!open && !save.isPending ? onClose() : undefined)}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <form onSubmit={submit} noValidate className="space-y-4" data-testid="canned-form">
          <div>
            <DialogTitle className="font-display text-xl font-semibold text-strong">{reply ? t("editTitle") : t("newTitle")}</DialogTitle>
            <DialogDescription className="mt-1 text-muted">{t("description")}</DialogDescription>
          </div>
          {problem ? <Alert variant="danger">{problem}</Alert> : null}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label={t("titleLabel")} htmlFor="cf-title" required error={errors.title}>
              <Input id="cf-title" value={form.title} maxLength={120} onChange={(e) => set("title", e.target.value)} aria-invalid={!!errors.title} />
            </Field>
            <Field label={t("language")} htmlFor="cf-language">
              <Select
                id="cf-language"
                value={form.language}
                onValueChange={(v) => set("language", v)}
                options={[
                  { value: "en", label: t("lang.en") },
                  { value: "bn", label: t("lang.bn") },
                ]}
                placeholder={t("language")}
                searchPlaceholder={tc("search")}
                emptyText={tc("noMatch")}
              />
            </Field>
            <Field label={t("categoryLabel")} htmlFor="cf-category" hint={t("categoryHint")}>
              <Input id="cf-category" value={form.category} maxLength={60} onChange={(e) => set("category", e.target.value)} />
            </Field>
            <div className="flex items-end">
              <label className="flex min-h-11 cursor-pointer items-center gap-3 text-base text-strong">
                <Switch checked={form.isActive} onCheckedChange={(v) => set("isActive", v)} aria-label={t("active")} />
                <span>{t("active")}</span>
              </label>
            </div>
          </div>

          <Field label={t("body")} htmlFor="cf-body" required error={errors.body} hint={t("bodyHint")}>
            <Textarea id="cf-body" ref={bodyRef} value={form.body} maxLength={MESSAGE_MAX} onChange={(e) => set("body", e.target.value)} className="min-h-32" aria-invalid={!!errors.body} />
          </Field>

          <div className="space-y-2">
            <p className="text-sm font-medium text-strong">{t("placeholders")}</p>
            <ul className="flex flex-wrap gap-2" aria-label={t("placeholders")}>
              {PLACEHOLDERS.map((name) => (
                <li key={name}>
                  <Button type="button" variant="secondary" onClick={() => insertPlaceholder(name)} aria-label={t("insert", { name })}>
                    <code className="font-mono text-sm">{`{${name}}`}</code>
                  </Button>
                </li>
              ))}
            </ul>
            <p className="text-sm text-muted">{t("placeholderHelp")}</p>
            {unknown.length > 0 ? <Alert variant="warning">{t("unknownPlaceholders", { names: unknown.map((n) => `{${n}}`).join(", ") })}</Alert> : null}
          </div>

          <div className="space-y-1">
            <p className="text-sm font-medium text-strong">{t("preview")}</p>
            <div className="rounded-control border border-line bg-sunken p-3" aria-live="polite" data-testid="canned-preview">
              <p className="whitespace-pre-wrap text-strong [overflow-wrap:anywhere]">{preview || <span className="text-muted">{t("previewEmpty")}</span>}</p>
            </div>
            <p className="text-sm text-muted">{t("previewNote")}</p>
          </div>

          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <DialogClose asChild>
              <Button variant="secondary" disabled={save.isPending}>
                {tc("cancel")}
              </Button>
            </DialogClose>
            <Button type="submit" loading={save.isPending} data-testid="canned-save">
              {t("save")}
            </Button>
          </div>
        </form>
      </ModalContent>
    </Dialog>
  );
}
