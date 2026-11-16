"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, CircleCheck, CirclePause, CirclePlay, CircleX, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Field, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useTicket, useTicketAction, type TicketDetail } from "./api";
import { PriorityBadge, StatusBadge, useAssigneeOptions, useCategoryLabel, useSupportPermissions } from "./common";
import { ReplyComposer } from "./reply-composer";
import { Thread } from "./thread";
import { TICKET_PRIORITIES, ageParts, allowedActions, needsConfirmation, threadCounts, type TicketAction, type TicketPriority } from "./logic";

const NONE = "__none";

/** One ticket for staff: the conversation (internal notes marked), who and what, the history, and the steps this person may take. */
export function TicketDetailView({ id }: { id: string }) {
  const t = useTranslations("support.detail");
  const tc = useTranslations("support.common");
  const tn = useTranslations("nav");
  const query = useTicket(id);
  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.support"), href: "/admin/support" },
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
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/support">
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

function Loaded({ ticket, crumbs }: { ticket: TicketDetail; crumbs: { label: string; href?: string }[] }) {
  const t = useTranslations("support.detail");
  const counts = React.useMemo(() => threadCounts(ticket.messages), [ticket.messages]);
  return (
    <>
      <PageHeader
        title={ticket.number}
        description={ticket.subject}
        crumbs={crumbs}
        actions={
          <>
            <StatusBadge status={ticket.status} />
            <PriorityBadge priority={ticket.priority} />
          </>
        }
      />
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,22rem)]">
        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>{t("conversation")}</CardTitle>
              <p className="text-sm text-muted">{t("counts", { public: counts.public, internal: counts.internal })}</p>
            </CardHeader>
            <CardContent>
              <Thread messages={ticket.messages} view="staff" customerLabel={ticket.customerName ?? ticket.customerMobile} />
            </CardContent>
          </Card>
          <Card>
            <CardContent className="pt-6">
              <ReplyComposer ticket={ticket} />
            </CardContent>
          </Card>
        </div>
        <div className="space-y-6">
          <ActionsCard ticket={ticket} />
          <HandlingCard ticket={ticket} />
          <CustomerCard ticket={ticket} />
          <HistoryCard ticket={ticket} />
        </div>
      </div>
    </>
  );
}

const ACTION_ICON = { inProgress: CirclePlay, waiting: CirclePause, resolve: CircleCheck, close: CircleX, reopen: RotateCcw } as const;

/** Status buttons driven by the API's nextStatuses and canReopen and this person's rights; resolve and close ask first. */
function ActionsCard({ ticket }: { ticket: TicketDetail }) {
  const t = useTranslations("support.actions");
  const tc = useTranslations("support.common");
  const problemMessage = useProblemMessage();
  const perms = useSupportPermissions(ticket.branchId);
  const run = useTicketAction(ticket.id);
  const [confirm, setConfirm] = React.useState<TicketAction | null>(null);
  const [reason, setReason] = React.useState("");
  const [problem, setProblem] = React.useState<string | null>(null);

  const states = React.useMemo(() => allowedActions(ticket, { reply: perms.reply, close: perms.close, assign: perms.assign }), [ticket, perms.reply, perms.close, perms.assign]);

  const exec = async (action: TicketAction, why?: string) => {
    setProblem(null);
    try {
      switch (action) {
        case "inProgress":
          await run.mutateAsync({ action: "status", status: "InProgress" });
          break;
        case "waiting":
          await run.mutateAsync({ action: "status", status: "Waiting" });
          break;
        case "resolve":
          await run.mutateAsync({ action: "resolve", reason: why });
          break;
        case "close":
          await run.mutateAsync({ action: "close", reason: why });
          break;
        case "reopen":
          await run.mutateAsync({ action: "reopen", reason: why });
          break;
      }
      toast.success(t(`done.${action}`));
      setConfirm(null);
      setReason("");
    } catch (e) {
      setProblem(problemMessage(e instanceof ApiError ? e.problem : null));
      setConfirm(null);
    }
  };

  const click = (action: TicketAction) => (needsConfirmation(action) || action === "reopen" ? (setReason(""), setConfirm(action)) : void exec(action));
  const blocked = states.filter((s) => !s.allowed);
  const windowBlocked = blocked.find((s) => s.reason === "window");

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        {problem ? <Alert variant="danger">{problem}</Alert> : null}
        {states.length === 0 ? <p className="text-muted">{t("none")}</p> : null}
        <div className="flex flex-wrap gap-2">
          {states
            .filter((s) => s.allowed)
            .map((s) => {
              const Icon = ACTION_ICON[s.action];
              return (
                <Button key={s.action} variant={s.action === "resolve" ? "primary" : "secondary"} onClick={() => click(s.action)} disabled={run.isPending} data-testid={`action-${s.action}`}>
                  <Icon aria-hidden /> {t(`label.${s.action}`)}
                </Button>
              );
            })}
        </div>
        {windowBlocked ? <Alert variant="warning">{t("windowExpired")}</Alert> : null}
        {blocked.some((s) => s.reason === "permission") ? <p className="text-sm text-muted">{t("noPermission")}</p> : null}
      </CardContent>

      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => {
          if (!open) setConfirm(null);
        }}
        title={confirm ? t(`confirm.${confirm}.title`) : ""}
        description={confirm ? t(`confirm.${confirm}.description`) : ""}
        confirmLabel={confirm ? t(`label.${confirm}`) : ""}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={confirm === "close"}
        loading={run.isPending}
        onConfirm={async () => {
          if (confirm) await exec(confirm, reason);
        }}
      >
        <Field label={t("reason")} htmlFor="action-reason" hint={t("reasonHint")}>
          <Textarea id="action-reason" value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} className="min-h-20" />
        </Field>
      </ConfirmDialog>
    </Card>
  );
}

/** Assignee and priority: applied as soon as a choice is made (each writes an internal note into the thread). */
function HandlingCard({ ticket }: { ticket: TicketDetail }) {
  const t = useTranslations("support.handling");
  const tc = useTranslations("support.common");
  const tp = useTranslations("support.priority");
  const dateTime = useDateTime();
  const problemMessage = useProblemMessage();
  const perms = useSupportPermissions(ticket.branchId);
  const run = useTicketAction(ticket.id);
  const assignees = useAssigneeOptions(perms.assign, ticket.assigneeId, ticket.assigneeName);
  const [problem, setProblem] = React.useState<string | null>(null);
  const [now] = React.useState(() => Date.now());

  const change = async (vars: { action: "assign"; assigneeId: string | null } | { action: "priority"; priority: TicketPriority }) => {
    setProblem(null);
    try {
      await run.mutateAsync(vars);
      toast.success(vars.action === "assign" ? t("assigned") : t("priorityChanged"));
    } catch (e) {
      setProblem(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  const age = ageParts(Math.max(0, (now - new Date(ticket.createdAt).getTime()) / 3_600_000));
  const categoryLabel = useCategoryLabel();
  const tch = useTranslations("support.channel");

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {problem ? <Alert variant="danger">{problem}</Alert> : null}
        {perms.assign ? (
          <>
            <Field label={t("assignee")} htmlFor="h-assignee">
              <Select id="h-assignee" value={ticket.assigneeId ?? NONE} onValueChange={(v) => void change({ action: "assign", assigneeId: v === NONE ? null : v })} options={[{ value: NONE, label: t("unassigned") }, ...assignees]} placeholder={t("assignee")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} disabled={run.isPending} />
            </Field>
            <Field label={t("priority")} htmlFor="h-priority">
              <Select id="h-priority" value={ticket.priority} onValueChange={(v) => void change({ action: "priority", priority: v as TicketPriority })} options={TICKET_PRIORITIES.map((p) => ({ value: p, label: tp(p) }))} placeholder={t("priority")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} disabled={run.isPending} />
            </Field>
          </>
        ) : (
          <dl className="space-y-3">
            <div>
              <dt className="text-sm text-muted">{t("assignee")}</dt>
              <dd className="font-medium text-strong">{ticket.assigneeName ?? t("unassigned")}</dd>
            </div>
            <div>
              <dt className="text-sm text-muted">{t("priority")}</dt>
              <dd>
                <PriorityBadge priority={ticket.priority} />
              </dd>
            </div>
          </dl>
        )}
        <dl className="space-y-3 border-t border-line pt-4" data-testid="ticket-facts">
          <div>
            <dt className="text-sm text-muted">{t("created")}</dt>
            <dd className="font-medium text-strong">{dateTime(ticket.createdAt)}</dd>
          </div>
          <div>
            <dt className="text-sm text-muted">{t("firstResponse")}</dt>
            <dd className="font-medium text-strong">{ticket.firstResponseAt ? dateTime(ticket.firstResponseAt) : t("noResponse")}</dd>
          </div>
          <div>
            <dt className="text-sm text-muted">{t("age")}</dt>
            <dd className="font-medium text-strong">{age.days > 0 ? t("ageDays", { days: age.days, hours: age.hours }) : t("ageHours", { hours: age.hours })}</dd>
          </div>
          <div>
            <dt className="text-sm text-muted">{t("channel")}</dt>
            <dd className="font-medium text-strong">{tch(ticket.channel)}</dd>
          </div>
          <div>
            <dt className="text-sm text-muted">{t("category")}</dt>
            <dd className="font-medium text-strong">{categoryLabel(ticket.category)}</dd>
          </div>
          {ticket.overdue ? (
            <div>
              <Badge variant="danger">{t("overdue")}</Badge>
            </div>
          ) : null}
          {Number(ticket.reopenCount) > 0 ? (
            <div>
              <dt className="text-sm text-muted">{t("reopened")}</dt>
              <dd className="font-medium text-strong">{t("reopenedTimes", { count: Number(ticket.reopenCount) })}</dd>
            </div>
          ) : null}
        </dl>
      </CardContent>
    </Card>
  );
}

function CustomerCard({ ticket }: { ticket: TicketDetail }) {
  const t = useTranslations("support.customer");
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent>
        <dl className="space-y-3">
          <div>
            <dt className="text-sm text-muted">{t("name")}</dt>
            <dd className="font-medium text-strong [overflow-wrap:anywhere]">{ticket.customerName ?? "-"}</dd>
          </div>
          <div>
            <dt className="text-sm text-muted">{t("mobile")}</dt>
            <dd className="font-medium text-strong">{ticket.customerMobile}</dd>
          </div>
          {ticket.customerEmail ? (
            <div>
              <dt className="text-sm text-muted">{t("email")}</dt>
              <dd className="font-medium text-strong [overflow-wrap:anywhere]">{ticket.customerEmail}</dd>
            </div>
          ) : null}
          <div>
            <dt className="text-sm text-muted">{t("account")}</dt>
            <dd className="font-medium text-strong">{ticket.customerId ? t("registered") : t("guest")}</dd>
          </div>
          <div>
            <dt className="text-sm text-muted">{t("order")}</dt>
            <dd>
              {ticket.orderId ? (
                <Link href={`/admin/orders/${ticket.orderId}`} prefetch={false} className="inline-flex min-h-11 items-center rounded-control font-mono font-semibold text-strong hover:text-primary hover:underline" data-testid="order-link">
                  {ticket.orderNumber}
                </Link>
              ) : (
                <span className="text-muted">{t("noOrder")}</span>
              )}
              {ticket.branchCode ? <span className="block text-sm text-muted">{t("branch", { code: ticket.branchCode })}</span> : <span className="block text-sm text-muted">{t("global")}</span>}
            </dd>
          </div>
        </dl>
      </CardContent>
    </Card>
  );
}

function HistoryCard({ ticket }: { ticket: TicketDetail }) {
  const t = useTranslations("support.history");
  const ts = useTranslations("support.status");
  const dateTime = useDateTime();
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent>
        <ol className="space-y-3" aria-label={t("title")} data-testid="ticket-history">
          {ticket.history.map((h, i) => (
            <li key={`${h.at}-${i}`} className="rounded-control bg-sunken p-3 text-sm">
              <p className="font-medium text-strong">{h.fromStatus ? t("move", { from: ts(h.fromStatus), to: ts(h.toStatus) }) : t("start", { to: ts(h.toStatus) })}</p>
              {h.reason ? <p className="text-strong [overflow-wrap:anywhere]">{h.reason}</p> : null}
              <p className="text-muted">
                {h.actorName ?? "-"} · {dateTime(h.at)}
              </p>
            </li>
          ))}
        </ol>
      </CardContent>
    </Card>
  );
}
