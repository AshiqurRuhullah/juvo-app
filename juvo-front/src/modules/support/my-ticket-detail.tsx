"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, RotateCcw, Send, XCircle } from "lucide-react";
import { Button, buttonVariants } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Textarea } from "@/components/ui/form";
import { Alert, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import type { UploadedFile } from "@/components/data/attachment-picker";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useMyTicket, useMyTicketAction, type MyTicket } from "./api";
import { StatusBadge, TicketAttachments, useCategoryLabel } from "./common";
import { MESSAGE_MAX } from "./logic";
import { Thread } from "./thread";

/** One ticket as the customer sees it: the conversation (staff appear as "Juvo BD Support", never internal notes), a reply box, close and reopen. */
export function MyTicketDetail({ id }: { id: string }) {
  const t = useTranslations("support.mineDetail");
  const tc = useTranslations("support.common");
  const tn = useTranslations("nav");
  const query = useMyTicket(id);
  const crumbs = [
    { label: tn("items.accountHome"), href: "/account" },
    { label: tn("items.mySupport"), href: "/account/support" },
    { label: query.data?.number ?? "..." },
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
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFound") : tc("loadFailed")}
          description={notFound ? t("notFoundHelp") : undefined}
          action={
            <Button asChild variant="secondary">
              <Link href="/account/support">
                <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
              </Link>
            </Button>
          }
        />
      </>
    );
  }
  return <Loaded ticket={query.data} crumbs={crumbs} />;
}

function Loaded({ ticket, crumbs }: { ticket: MyTicket; crumbs: { label: string; href?: string }[] }) {
  const t = useTranslations("support.mineDetail");
  const dateTime = useDateTime();
  const categoryLabel = useCategoryLabel();
  const problemMessage = useProblemMessage();
  const run = useMyTicketAction(ticket.id);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [closing, setClosing] = React.useState(false);
  const isClosed = ticket.status === "Closed";
  const isEnded = ticket.status === "Resolved" || isClosed;

  const act = async (vars: Parameters<typeof run.mutateAsync>[0], doneText: string) => {
    setProblem(null);
    try {
      await run.mutateAsync(vars);
      toast.success(doneText);
    } catch (e) {
      setProblem(problemMessage(e instanceof ApiError ? e.problem : null));
    } finally {
      setClosing(false);
    }
  };

  return (
    <>
      <PageHeader
        title={ticket.number}
        description={ticket.subject}
        crumbs={crumbs}
        actions={
          <>
            <StatusBadge status={ticket.status} />
            {ticket.canClose ? (
              <Button variant="secondary" onClick={() => setClosing(true)} data-testid="my-close">
                <XCircle aria-hidden /> {t("close")}
              </Button>
            ) : null}
            {ticket.canReopen ? (
              <Button variant="secondary" loading={run.isPending} onClick={() => void act({ action: "reopen" }, t("reopened"))} data-testid="my-reopen">
                <RotateCcw aria-hidden /> {t("reopen")}
              </Button>
            ) : null}
          </>
        }
      />
      <div className="space-y-6">
        <p className="text-sm text-muted">
          {categoryLabel(ticket.category)}
          {ticket.orderNumber ? ` · ${t("order", { number: ticket.orderNumber })}` : ""} · {t("opened", { date: dateTime(ticket.createdAt) })}
        </p>
        {problem ? <Alert variant="danger">{problem}</Alert> : null}
        {isEnded && !ticket.canReopen ? (
          <Alert variant="info" data-testid="no-reopen">
            {isClosed ? t("closedNoReopen") : t("resolvedNoReopen")}{" "}
            <Link href="/account/support/new" className={buttonVariants({ variant: "ghost" })}>
              {t("startNew")}
            </Link>
          </Alert>
        ) : null}
        {ticket.status === "Resolved" && ticket.canReopen ? <Alert variant="success">{t("resolvedNotice")}</Alert> : null}
        <Card>
          <CardHeader>
            <CardTitle>{t("conversation")}</CardTitle>
          </CardHeader>
          <CardContent>
            <Thread messages={ticket.messages} view="customer" />
          </CardContent>
        </Card>
        {ticket.canReply ? <ReplyBox ticket={ticket} /> : null}
      </div>

      <ConfirmDialog
        open={closing}
        onOpenChange={setClosing}
        title={t("closeTitle")}
        description={t("closeDescription")}
        confirmLabel={t("close")}
        cancelLabel={t("cancel")}
        closeLabel={t("dismiss")}
        loading={run.isPending}
        onConfirm={() => act({ action: "close" }, t("closed"))}
      />
    </>
  );
}

function ReplyBox({ ticket }: { ticket: MyTicket }) {
  const t = useTranslations("support.mineDetail");
  const problemMessage = useProblemMessage();
  const run = useMyTicketAction(ticket.id);
  const [text, setText] = React.useState("");
  const [files, setFiles] = React.useState<UploadedFile[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [empty, setEmpty] = React.useState(false);
  const sending = React.useRef(false);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (sending.current) return;
    if (text.trim() === "") {
      setEmpty(true);
      return;
    }
    sending.current = true;
    setError(null);
    try {
      await run.mutateAsync({ action: "reply", text, attachmentPaths: files.map((f) => f.path!).filter(Boolean) });
      toast.success(t("replySent"));
      setText("");
      setFiles([]);
    } catch (e) {
      setError(problemMessage(e instanceof ApiError ? e.problem : null));
    } finally {
      sending.current = false;
    }
  };

  return (
    <Card>
      <CardContent className="pt-6">
        <form onSubmit={submit} noValidate className="space-y-3" data-testid="my-reply-form">
          <h2 className="font-display text-lg font-semibold text-strong">{t("replyTitle")}</h2>
          {ticket.status === "Resolved" ? <p className="text-sm text-muted">{t("replyReopens")}</p> : null}
          {error ? <Alert variant="danger">{error}</Alert> : null}
          <Field label={t("replyLabel")} htmlFor="my-reply" error={empty ? t("required") : undefined}>
            <Textarea
              id="my-reply"
              value={text}
              maxLength={MESSAGE_MAX}
              onChange={(e) => {
                setText(e.target.value);
                setEmpty(false);
              }}
              className="min-h-28"
              aria-invalid={empty}
            />
          </Field>
          <TicketAttachments scope="customer" idBase="my-reply" added={files} onAdded={setFiles} onError={(e) => setError(problemMessage(e instanceof ApiError ? e.problem : null))} disabled={run.isPending} />
          <Button type="submit" loading={run.isPending} data-testid="my-reply-send">
            <Send className="rtl:-scale-x-100" aria-hidden /> {t("send")}
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}
