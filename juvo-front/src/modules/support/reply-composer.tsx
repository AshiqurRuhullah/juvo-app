"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import { useTranslations } from "next-intl";
import { MessageSquareText, Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, Switch, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import type { UploadedFile } from "@/components/data/attachment-picker";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useTicketAction, type TicketDetail } from "./api";
import { TicketAttachments, useSupportPermissions } from "./common";
import { MESSAGE_MAX, canPublicReply, replyStatusChoices, type TicketStatus } from "./logic";

// The saved-reply picker is fetched only when someone asks for it.
const CannedPicker = dynamic(() => import("./canned-picker"), { ssr: false, loading: () => <Skeleton className="h-24" aria-busy="true" /> });

const KEEP = "__keep";

/** Reply box: public reply or internal note (a switch, with a visible label), saved replies, attachments and an optional status to set. */
export function ReplyComposer({ ticket }: { ticket: TicketDetail }) {
  const t = useTranslations("support.composer");
  const ts = useTranslations("support.status");
  const tc = useTranslations("support.common");
  const problemMessage = useProblemMessage();
  const perms = useSupportPermissions(ticket.branchId);
  const action = useTicketAction(ticket.id);
  const [internal, setInternal] = React.useState(false);
  const [text, setText] = React.useState("");
  const [files, setFiles] = React.useState<UploadedFile[]>([]);
  const [status, setStatus] = React.useState(KEEP);
  const [canned, setCanned] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const [textError, setTextError] = React.useState(false);
  const sending = React.useRef(false);

  const publicAllowed = canPublicReply(ticket.status);
  // A closed ticket takes only internal notes: the API answers TICKET.CLOSED for a public reply.
  const mode = publicAllowed ? internal : true;
  const choices = React.useMemo(() => replyStatusChoices(ticket, { reply: perms.reply, close: perms.close, assign: perms.assign }), [ticket, perms.reply, perms.close, perms.assign]);

  const insert = (value: string) => {
    setText((old) => (old.trim() === "" ? value : `${old.replace(/\s+$/, "")}\n\n${value}`));
    setTextError(false);
  };

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (sending.current) return;
    if (text.trim() === "") {
      setTextError(true);
      return;
    }
    sending.current = true;
    setError(null);
    try {
      await action.mutateAsync({
        action: "reply",
        text,
        internal: mode,
        setStatus: !mode && status !== KEEP ? (status as TicketStatus) : null,
        attachmentPaths: files.map((f) => f.path!).filter(Boolean),
      });
      toast.success(mode ? t("noteAdded") : t("sent"));
      setText("");
      setFiles([]);
      setStatus(KEEP);
      setCanned(false);
    } catch (e) {
      setError(problemMessage(e instanceof ApiError ? e.problem : null));
    } finally {
      sending.current = false;
    }
  };

  if (!perms.reply) return <Alert variant="info">{t("noPermission")}</Alert>;

  return (
    <form onSubmit={submit} noValidate className="space-y-3" data-testid="reply-composer" aria-label={t("title")}>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 className="font-display text-lg font-semibold text-strong">{t("title")}</h2>
        <label className="flex min-h-11 cursor-pointer items-center gap-3 text-base font-medium text-strong">
          <Switch checked={mode} onCheckedChange={setInternal} disabled={!publicAllowed} aria-label={t("internalSwitch")} data-testid="internal-switch" />
          <span>{mode ? t("internalNote") : t("publicReply")}</span>
        </label>
      </div>
      {!publicAllowed ? <Alert variant="info">{t("closedNotice")}</Alert> : null}
      {mode ? <Alert variant="warning">{t("internalHelp")}</Alert> : <p className="text-sm text-muted">{t("publicHelp")}</p>}
      {error ? <Alert variant="danger">{error}</Alert> : null}

      <Field label={mode ? t("noteLabel") : t("replyLabel")} htmlFor="reply-text" error={textError ? t("required") : undefined}>
        <Textarea
          id="reply-text"
          value={text}
          maxLength={MESSAGE_MAX}
          onChange={(e) => {
            setText(e.target.value);
            setTextError(false);
          }}
          className="min-h-32"
          aria-invalid={textError}
          data-testid="reply-text"
        />
      </Field>

      <div className="flex flex-wrap gap-2">
        {perms.cannedView && !mode ? (
          <Button type="button" variant="secondary" onClick={() => setCanned((v) => !v)} aria-expanded={canned} data-testid="canned-toggle">
            <MessageSquareText aria-hidden /> {t("savedReply")}
          </Button>
        ) : null}
      </div>
      {canned && !mode ? <CannedPicker ticketId={ticket.id} onInsert={insert} /> : null}

      <TicketAttachments scope="staff" idBase="reply" added={files} onAdded={setFiles} onError={(e) => setError(problemMessage(e instanceof ApiError ? e.problem : null))} disabled={action.isPending} />

      {!mode && choices.length > 0 ? (
        <Field label={t("setStatus")} htmlFor="reply-status" hint={t("setStatusHint")}>
          <Select id="reply-status" value={status} onValueChange={setStatus} options={[{ value: KEEP, label: t("keepStatus") }, ...choices.map((c) => ({ value: c, label: ts(c) }))]} placeholder={t("setStatus")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
        </Field>
      ) : null}

      <Button type="submit" loading={action.isPending} data-testid="reply-send">
        <Send className="rtl:-scale-x-100" aria-hidden /> {mode ? t("addNote") : t("send")}
      </Button>
    </form>
  );
}
