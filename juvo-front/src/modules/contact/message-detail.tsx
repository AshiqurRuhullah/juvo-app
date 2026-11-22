"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Archive, ArchiveRestore, ArrowLeft, Eye, Lock, Mail, NotebookPen, CheckCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Textarea } from "@/components/ui/form";
import { PageHeader } from "@/components/layout/page-header";
import { Alert, Card, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useContentProblem } from "@/modules/content/common";
import { useAddNote, useArchive, useMessage, useSetStatus } from "./api";
import { MessageStatusBadge, useInboxRights } from "./common";
import { LIMITS, canArchive, statusChoices, type MessageStatus } from "./logic";

const STATUS_ICON = { New: Mail, Read: Eye, Replied: CheckCheck, Archived: Archive } as const;

/** One message: the text, the sender's details as the API sends them, status buttons, internal notes, archive and restore. */
export function MessageDetailView({ id }: { id: string }) {
  const t = useTranslations("contact.detail");
  const tn = useTranslations("nav");
  const ts = useTranslations("contact.status");
  const tnote = useTranslations("contact.detail.noteErrors");
  const dateTime = useDateTime();
  const rights = useInboxRights();
  const problem = useContentProblem();
  const query = useMessage(id);
  const setStatus = useSetStatus(id);
  const addNote = useAddNote(id);
  const archive = useArchive(id);
  const [note, setNote] = React.useState("");
  const [noteError, setNoteError] = React.useState<string | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);
  const busy = setStatus.isPending || archive.isPending;

  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.contentMessages"), href: "/admin/content/messages" },
    { label: query.data?.reference ?? "..." },
  ];

  if (query.isPending) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy="true" />
      </>
    );
  }
  if (query.isError || !query.data) {
    const missing = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("crumb")} crumbs={crumbs} />
        <EmptyState
          title={missing ? t("notFound") : t("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/content/messages">
                <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
              </Link>
            </Button>
          }
        />
      </>
    );
  }

  const m = query.data;
  const run = async (action: () => Promise<unknown>, done: string) => {
    setFailure(null);
    try {
      await action();
      toast.success(done);
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null).text);
    }
  };

  const changeStatus = (status: MessageStatus) =>
    run(() => setStatus.mutateAsync(status), status === "New" && m.status === "Archived" ? t("restored") : t("statusSaved", { status: ts(status) }));

  const submitNote = async (event: React.FormEvent) => {
    event.preventDefault();
    const text = note.trim();
    if (!text) return setNoteError(tnote("required"));
    if (text.length > LIMITS.note) return setNoteError(tnote("tooLong", { max: LIMITS.note }));
    setNoteError(null);
    setFailure(null);
    try {
      await addNote.mutateAsync(text);
      setNote("");
      toast.success(t("noteSaved"));
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null).text);
    }
  };

  const label = (status: MessageStatus) => {
    if (status === "New") return m.status === "Archived" ? t("restore") : t("markNew");
    return status === "Read" ? t("markRead") : t("markReplied");
  };

  return (
    <>
      <PageHeader title={m.subject || t("noSubject")} description={`${m.reference} · ${dateTime(m.receivedAt)}`} crumbs={crumbs} />
      <div className="grid gap-6 lg:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
        <div className="space-y-6">
          <Card className="space-y-4 p-5" data-testid="message-card">
            <div className="flex flex-wrap items-center gap-2">
              <MessageStatusBadge status={m.status} />
            </div>
            <dl className="grid gap-3 sm:grid-cols-2">
              <Item label={t("from")} value={m.name} testId="message-name" />
              <Item label={t("received")} value={dateTime(m.receivedAt)} />
              {m.mobile ? <Item label={t("mobile")} value={m.mobile} testId="message-mobile" /> : null}
              {m.email ? <Item label={t("email")} value={m.email} testId="message-email" /> : null}
              <Item label={t("subject")} value={m.subject || t("noSubject")} />
              {m.sourcePage ? <Item label={t("source")} value={m.sourcePage} /> : null}
            </dl>
            {m.piiMasked ? (
              <Alert variant="info" data-testid="pii-masked">
                <span className="flex items-start gap-2">
                  <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
                  <span>{t("piiMasked")}</span>
                </span>
              </Alert>
            ) : null}
            <div>
              <h2 className="mb-1 font-sans text-sm font-semibold text-strong">{t("messageHeading")}</h2>
              <p className="whitespace-pre-wrap text-strong [overflow-wrap:anywhere]" data-testid="message-text">
                {m.message}
              </p>
            </div>
            <p className="text-sm text-muted" data-testid="message-handled">
              {m.handledAt ? t("handled", { when: dateTime(m.handledAt) }) : t("notHandled")}
            </p>
          </Card>

          <Card className="space-y-4 p-5">
            <h2 className="flex items-center gap-2 font-sans text-base font-semibold text-strong">
              <NotebookPen className="size-5" aria-hidden /> {t("noteHeading")}
            </h2>
            <p className="text-sm text-muted">{t("noteHelp")}</p>
            {m.internalNote?.trim() ? (
              <p className="whitespace-pre-wrap rounded-control bg-sunken p-3 text-strong [overflow-wrap:anywhere]" data-testid="message-notes">
                {m.internalNote}
              </p>
            ) : (
              <p className="text-muted" data-testid="message-notes-empty">
                {t("noNotes")}
              </p>
            )}
            {rights.handle ? (
              <form onSubmit={submitNote} noValidate className="space-y-3">
                <Field label={t("noteLabel")} htmlFor="msg-note" error={noteError ?? undefined}>
                  <Textarea id="msg-note" value={note} maxLength={LIMITS.note} placeholder={t("notePlaceholder")} onChange={(e) => setNote(e.target.value)} className="min-h-28" aria-invalid={!!noteError} />
                </Field>
                <Button type="submit" loading={addNote.isPending} data-testid="note-add">
                  {t("noteAdd")}
                </Button>
              </form>
            ) : null}
          </Card>
        </div>

        <div className="space-y-4">
          {failure ? (
            <Alert variant="danger" data-testid="message-failure">
              {failure}
            </Alert>
          ) : null}
          {rights.handle ? (
            <Card className="space-y-3 p-5" data-testid="message-actions">
              <h2 className="font-sans text-base font-semibold text-strong">{t("actionsHeading")}</h2>
              <p className="text-sm text-muted">
                {t("currentStatus")}: <span className="font-medium text-strong">{ts(m.status)}</span>
              </p>
              <div className="flex flex-col gap-2">
                {statusChoices(m.status).map((status) => {
                  const Icon = m.status === "Archived" && status === "New" ? ArchiveRestore : STATUS_ICON[status];
                  return (
                    <Button key={status} variant="secondary" disabled={busy} onClick={() => changeStatus(status)} data-testid={`set-${status}`}>
                      <Icon aria-hidden /> {label(status)}
                    </Button>
                  );
                })}
                {canArchive(m.status) ? (
                  <Button variant="secondary" disabled={busy} data-testid="archive" onClick={() => run(() => archive.mutateAsync(), t("archived"))}>
                    <Archive aria-hidden /> {t("archive")}
                  </Button>
                ) : null}
              </div>
            </Card>
          ) : (
            <Alert variant="info" data-testid="message-read-only">
              <span className="flex items-start gap-2">
                <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
                <span>{t("readOnly")}</span>
              </span>
            </Alert>
          )}
          <Button asChild variant="ghost">
            <Link href="/admin/content/messages">
              <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
            </Link>
          </Button>
        </div>
      </div>
    </>
  );
}

function Item({ label, value, testId }: { label: string; value: string; testId?: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="font-medium text-strong [overflow-wrap:anywhere]" data-testid={testId}>
        {value}
      </dd>
    </div>
  );
}
