"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Archive, CheckCheck, Eye, Mail } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import type { MessageStatus } from "./logic";

/** What this staff member may do in the contact inbox (global permissions). Display only: the API still refuses. */
export function useInboxRights() {
  const { me } = useSession();
  return React.useMemo(() => ({ view: canAnywhere(me, "content.message.view"), handle: canAnywhere(me, "content.message.handle") }), [me]);
}

const ICON = { New: Mail, Read: Eye, Replied: CheckCheck, Archived: Archive } as const;
const TONE = { New: "brand", Read: "neutral", Replied: "success", Archived: "outline" } as const;

/** Status as an icon and a word. */
export function MessageStatusBadge({ status }: { status: MessageStatus }) {
  const t = useTranslations("contact.status");
  const Icon = ICON[status];
  return (
    <Badge variant={TONE[status]} data-testid="message-status" data-status={status}>
      <Icon className="size-3.5" aria-hidden />
      {t(status)}
    </Badge>
  );
}
