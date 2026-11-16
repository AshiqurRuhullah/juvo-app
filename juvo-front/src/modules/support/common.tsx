"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleAlert, CircleCheck, CircleDot, CirclePause, CircleX, Flame } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import type { SelectOption } from "@/components/ui/select";
import { AttachmentPicker, type AttachmentLabels, type UploadedFile } from "@/components/data/attachment-picker";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere, canInBranch } from "@/lib/auth/permissions";
import { mediaUrl } from "@/lib/media";
import { ATTACHMENT_MAX_FILES, TICKET_CATEGORIES, attachmentProblem, priorityTone, statusTone, type TicketPriority, type TicketStatus } from "./logic";
import { uploadTicketFile, useStaffForAssign } from "./api";

/** What this staff member may do with tickets. Pass the ticket's branch id to check in that branch (a global ticket: anywhere). Display only: the API still refuses. */
export function useSupportPermissions(branchId?: string | null) {
  const { me } = useSession();
  return React.useMemo(() => {
    const has = (code: string) => (branchId ? canInBranch(me, code, branchId) : canAnywhere(me, code));
    return {
      view: has("support.ticket.view"),
      create: has("support.ticket.create"),
      assign: has("support.ticket.assign"),
      reply: has("support.ticket.reply"),
      close: has("support.ticket.close"),
      cannedView: canAnywhere(me, "support.canned-reply.view"),
      cannedEdit: canAnywhere(me, "support.canned-reply.edit"),
      reportView: canAnywhere(me, "support.report.view"),
      userId: me.staff?.id ?? null,
      userName: me.staff?.fullName ?? null,
      canListUsers: canAnywhere(me, "ums.user.view"),
    };
  }, [me, branchId]);
}

const STATUS_ICON: Record<TicketStatus, React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>> = {
  Open: CircleDot,
  InProgress: CircleDot,
  Waiting: CirclePause,
  Resolved: CircleCheck,
  Closed: CircleX,
};

/** Status as a badge with a word (and an icon), so colour is never the only signal. */
export function StatusBadge({ status }: { status: TicketStatus }) {
  const t = useTranslations("support.status");
  const Icon = STATUS_ICON[status];
  return (
    <Badge variant={statusTone(status)} data-testid="ticket-status">
      <Icon className="size-3.5" aria-hidden />
      {t(status)}
    </Badge>
  );
}

/** Priority badge. Urgent and High carry an icon as well as the word. */
export function PriorityBadge({ priority }: { priority: TicketPriority }) {
  const t = useTranslations("support.priority");
  return (
    <Badge variant={priorityTone(priority)} data-testid="ticket-priority">
      {priority === "Urgent" ? <Flame className="size-3.5" aria-hidden /> : priority === "High" ? <CircleAlert className="size-3.5" aria-hidden /> : null}
      {t(priority)}
    </Badge>
  );
}

/** Category label (an unknown code from older data is shown as it is). */
export function useCategoryLabel() {
  const t = useTranslations("support.category");
  return React.useCallback((code: string | null | undefined) => (code && t.has(code) ? t(code) : (code ?? "-")), [t]);
}

export function useCategoryOptions(): SelectOption[] {
  const t = useTranslations("support.category");
  return React.useMemo(() => TICKET_CATEGORIES.map((c) => ({ value: c, label: t(c) })), [t]);
}

/**
 * People a ticket can be assigned to. The API has no list of assignable staff, so this uses the staff list when the person may view
 * users, and always offers "me". The API checks the choice again (`TICKET.ASSIGNEE_NOT_FOUND`). Loaded only when `enabled`.
 */
export function useAssigneeOptions(enabled: boolean, extraId?: string | null, extraName?: string | null): SelectOption[] {
  const perms = useSupportPermissions();
  const users = useStaffForAssign(enabled && perms.canListUsers);
  const t = useTranslations("support.common");
  return React.useMemo(() => {
    const options = new Map<string, string>();
    if (perms.userId) options.set(perms.userId, t("assignMe", { name: perms.userName ?? "" }));
    if (perms.canListUsers) for (const u of users.data?.items ?? []) if (!options.has(u.id)) options.set(u.id, u.fullName);
    if (extraId && !options.has(extraId)) options.set(extraId, extraName ?? extraId);
    return [...options].map(([value, label]) => ({ value, label }));
  }, [perms.userId, perms.userName, perms.canListUsers, users.data, extraId, extraName, t]);
}

/** Attach button and list for staff or customer messages: client checks (type and size), at most 5 files, upload one by one. */
export function TicketAttachments({
  scope,
  added,
  onAdded,
  onError,
  disabled,
  idBase,
}: {
  scope: "staff" | "customer";
  added: readonly UploadedFile[];
  onAdded: (files: UploadedFile[]) => void;
  onError: (error: unknown) => void;
  disabled?: boolean;
  idBase: string;
}) {
  const t = useTranslations("support.attach");
  const labels: AttachmentLabels = {
    add: t("add"),
    uploading: t("uploading"),
    remove: (name) => t("remove", { name }),
    hint: t("hint", { max: ATTACHMENT_MAX_FILES }),
    typeError: t("type"),
    sizeError: t("size"),
    open: (name) => t("open", { name }),
  };
  const check = React.useCallback(
    (file: File) => attachmentProblem(file),
    [],
  );
  return (
    <AttachmentPicker
      idBase={idBase}
      added={added}
      onAdded={(files) => onAdded(files.slice(0, ATTACHMENT_MAX_FILES))}
      upload={(file) => uploadTicketFile(file, scope)}
      check={check}
      labels={labels}
      onError={onError}
      disabled={disabled || added.length >= ATTACHMENT_MAX_FILES}
    />
  );
}

/** Message attachments as links (pictures and PDFs), shared by the staff and customer threads. */
export function AttachmentLinks({ items }: { items: { id: string; fileName: string; url: string }[] }) {
  const t = useTranslations("support.attach");
  if (items.length === 0) return null;
  return (
    <ul className="mt-2 flex flex-wrap gap-2" aria-label={t("list")}>
      {items.map((a) => (
        <li key={a.id}>
          <a href={mediaUrl(a.url)} target="_blank" rel="noopener noreferrer" className="inline-flex min-h-11 items-center rounded-control border border-line bg-card px-3 text-sm font-medium text-primary hover:underline" aria-label={t("open", { name: a.fileName })}>
            <span className="[overflow-wrap:anywhere]">{a.fileName}</span>
          </a>
        </li>
      ))}
    </ul>
  );
}
