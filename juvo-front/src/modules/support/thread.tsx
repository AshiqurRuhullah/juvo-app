"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Headset, Lock, User } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { cn } from "@/lib/utils";
import { AttachmentLinks } from "./common";
import { groupByDay, threadView, type ThreadMessage } from "./logic";

/**
 * The conversation of one ticket, oldest first, grouped by day. Staff view (`staff`) draws internal notes in a different box with a
 * label and a lock (they are never customer-visible); the customer view (`customer`) only ever receives customer and staff messages.
 * Messages are memoised so a long thread stays responsive while typing in the composer.
 */
export function Thread({ messages, view, customerLabel }: { messages: readonly ThreadMessage[]; view: "staff" | "customer"; customerLabel?: string }) {
  const format = useFormatter();
  const dayLabel = React.useCallback((iso: string) => format.dateTime(new Date(iso), { dateStyle: "medium" }), [format]);
  const days = React.useMemo(() => groupByDay(messages, (iso) => format.dateTime(new Date(iso), { year: "numeric", month: "2-digit", day: "2-digit" })), [messages, format]);
  return (
    <div className="space-y-5" data-testid="ticket-thread">
      {days.map((group) => (
        <section key={group.day} aria-label={dayLabel(group.messages[0]!.at)}>
          <h3 className="mb-3 text-center text-xs font-semibold uppercase tracking-wide text-muted">{dayLabel(group.messages[0]!.at)}</h3>
          <ol className="space-y-3">
            {group.messages.map((m) => (
              <li key={m.id}>
                <Message message={m} view={view} customerLabel={customerLabel} />
              </li>
            ))}
          </ol>
        </section>
      ))}
    </div>
  );
}

const Message = React.memo(function Message({ message, view, customerLabel }: { message: ThreadMessage; view: "staff" | "customer"; customerLabel?: string }) {
  const t = useTranslations("support.thread");
  const format = useFormatter();
  const kind = threadView(message.kind);
  const time = format.dateTime(new Date(message.at), { timeStyle: "short" });

  if (kind === "system") {
    return (
      <p className="rounded-control bg-sunken px-3 py-2 text-center text-sm text-muted [overflow-wrap:anywhere]" data-testid="message-system">
        {message.text} <span className="whitespace-nowrap">· {time}</span>
      </p>
    );
  }

  const mine = view === "customer" ? kind === "customer" : kind === "staff" || kind === "internal";
  const author = kind === "customer" ? (view === "customer" ? t("you") : (message.authorName ?? customerLabel ?? t("customer"))) : view === "customer" ? t("support") : (message.authorName ?? t("staff"));

  if (kind === "internal") {
    return (
      <article className="ms-auto max-w-[min(100%,40rem)] rounded-card border-2 border-dashed border-warning bg-warning-soft p-4" data-testid="message-internal" aria-label={t("internalLabel")}>
        <header className="mb-2 flex flex-wrap items-center gap-2">
          <Badge variant="warning">
            <Lock className="size-3.5" aria-hidden /> {t("internalBadge")}
          </Badge>
          <span className="text-sm font-semibold text-strong">{author}</span>
          <span className="text-sm text-muted">{time}</span>
        </header>
        <p className="whitespace-pre-wrap text-strong [overflow-wrap:anywhere]">{message.text}</p>
        <AttachmentLinks items={message.attachments} />
      </article>
    );
  }

  return (
    <article
      className={cn("max-w-[min(100%,40rem)] rounded-card border p-4", mine ? "ms-auto border-primary/20 bg-primary-soft" : "me-auto border-line bg-card")}
      data-testid={kind === "customer" ? "message-customer" : "message-staff"}
    >
      <header className="mb-2 flex flex-wrap items-center gap-2">
        {kind === "customer" ? <User className="size-4 text-muted" aria-hidden /> : <Headset className="size-4 text-primary-dark" aria-hidden />}
        <span className="text-sm font-semibold text-strong">{author}</span>
        <span className="text-sm text-muted">{time}</span>
        {view === "staff" ? <Badge variant="outline">{kind === "customer" ? t("fromCustomer") : t("publicReply")}</Badge> : null}
      </header>
      <p className="whitespace-pre-wrap text-strong [overflow-wrap:anywhere]">{message.text}</p>
      <AttachmentLinks items={message.attachments} />
    </article>
  );
});
