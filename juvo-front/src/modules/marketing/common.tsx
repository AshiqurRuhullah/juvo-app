"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { CircleCheck, CircleDot, CircleX, Clock, Mail, MessageSquare, PencilLine, Send } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import { smsLength, statusTone, type CampaignStatus } from "./logic";

/** What this staff member may do in marketing. All marketing permissions are global (no branch). Display only: the API still refuses. */
export function useMarketingPermissions() {
  const { me } = useSession();
  return React.useMemo(() => {
    const has = (code: string) => canAnywhere(me, code);
    return {
      segmentView: has("marketing.segment.view"),
      segmentEdit: has("marketing.segment.edit"),
      campaignView: has("marketing.campaign.view"),
      campaignCreate: has("marketing.campaign.create"),
      campaignSend: has("marketing.campaign.send"),
      automationView: has("marketing.automation.view"),
      automationEdit: has("marketing.automation.edit"),
      catalogView: has("catalog.product.view"),
    };
  }, [me]);
}

const STATUS_ICON: Record<CampaignStatus, React.ComponentType<{ className?: string; "aria-hidden"?: boolean }>> = {
  Draft: PencilLine,
  Scheduled: Clock,
  Sending: Send,
  Sent: CircleCheck,
  Cancelled: CircleX,
};

/** Campaign status as a badge with an icon and a word, so colour is never the only signal. */
export function CampaignStatusBadge({ status }: { status: CampaignStatus }) {
  const t = useTranslations("marketing.status");
  const Icon = STATUS_ICON[status] ?? CircleDot;
  return (
    <Badge variant={statusTone(status)} data-testid="campaign-status">
      <Icon className="size-3.5" aria-hidden />
      {t(status)}
    </Badge>
  );
}

export function ChannelBadge({ channel }: { channel: string }) {
  const t = useTranslations("marketing.channel");
  const Icon = channel === "Email" ? Mail : MessageSquare;
  return (
    <Badge variant="outline">
      <Icon className="size-3.5" aria-hidden />
      {t.has(channel) ? t(channel) : channel}
    </Badge>
  );
}

/** A reason code (NoConsent ...) in the current language; an unknown code is shown as it is. */
export function useReasonLabel() {
  const t = useTranslations("marketing.reasons");
  return React.useCallback((reason: string | null | undefined) => (reason ? (t.has(reason) ? t(reason) : reason) : "-"), [t]);
}

/** Characters, and for SMS the number of parts, under a text box. Linked to the box with aria-describedby by the caller. */
export function TextCounter({ id, text, sms }: { id: string; text: string; sms: boolean }) {
  const t = useTranslations("marketing.counter");
  if (!sms) return (
    <p id={id} className="text-sm text-muted">
      {t("characters", { count: text.length })}
    </p>
  );
  const length = smsLength(text);
  return (
    <p id={id} className="text-sm text-muted" data-testid="sms-counter">
      {t("sms", { count: length.characters, parts: length.parts, encoding: t(length.encoding), perPart: length.perPart })}
    </p>
  );
}

/** One number with its label, used in result panels. */
export function Stat({ label, value, hint, testId }: { label: string; value: React.ReactNode; hint?: string; testId?: string }) {
  return (
    <div className="rounded-card border border-line bg-card p-3" data-testid={testId}>
      <p className="text-2xl font-bold tabular-nums text-strong">{value}</p>
      <p className="text-sm text-muted">{label}</p>
      {hint ? <p className="mt-1 text-xs text-muted">{hint}</p> : null}
    </div>
  );
}
