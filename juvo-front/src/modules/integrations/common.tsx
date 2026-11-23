"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Check, CircleCheck, CircleSlash, CircleX, Clock, Copy, Hourglass, TriangleAlert } from "lucide-react";
import { Badge } from "@/components/ui/surfaces";
import { Button } from "@/components/ui/button";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere } from "@/lib/auth/permissions";
import type { ApiProblem } from "@/lib/api/types";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { DELIVERY_TONE, eventKey, isKnownEvent, isKnownScope, scopeKey, type DeliveryStatus, type KeyState } from "./logic";

/** What this staff member may do (platform-wide, not per branch). Display only: the API still refuses. */
export function useIntegrationRights() {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      keysView: canAnywhere(me, "integrations.apikey.view"),
      keysManage: canAnywhere(me, "integrations.apikey.manage"),
      hooksView: canAnywhere(me, "integrations.webhook.view"),
      hooksManage: canAnywhere(me, "integrations.webhook.manage"),
    }),
    [me],
  );
}

/**
 * Staff wording for a refused request. Field messages the API wrote (for example why an address was refused) are shown as they come,
 * because they say what to change; then our texts for the codes of this module; then the shared texts.
 */
export function useIntegrationProblem() {
  const t = useTranslations("integrations.errors");
  const fallback = useProblemMessage();
  return (problem: ApiProblem | null | undefined): string => {
    const fields = Object.values(problem?.errors ?? {}).flat();
    if (problem?.code === "INTEGRATIONS.URL_REFUSED" && fields.length) return fields.join(" ");
    const key = problem?.code?.replaceAll(".", "_");
    if (key && t.has(key)) return t(key);
    return fields.length ? fields.join(" ") : fallback(problem);
  };
}

const KEY_LOOK: Record<KeyState, { tone: "success" | "neutral" | "warning"; Icon: typeof CircleCheck }> = {
  Enabled: { tone: "success", Icon: CircleCheck },
  Disabled: { tone: "neutral", Icon: CircleSlash },
  Expired: { tone: "warning", Icon: Hourglass },
};

/** Key state as a chip with a word and an icon, so colour is never the only signal. */
export function KeyStateBadge({ state }: { state: KeyState }) {
  const t = useTranslations("integrations.keys.state");
  const { tone, Icon } = KEY_LOOK[state];
  return (
    <Badge variant={tone} data-testid="key-state" data-state={state}>
      <Icon className="size-3.5" aria-hidden />
      {t(state)}
    </Badge>
  );
}

const DELIVERY_ICON = { Pending: Clock, Succeeded: CircleCheck, Failed: CircleX } as const;

export function DeliveryStatusBadge({ status }: { status: DeliveryStatus }) {
  const t = useTranslations("integrations.log.status");
  const Icon = DELIVERY_ICON[status];
  return (
    <Badge variant={DELIVERY_TONE[status]} data-testid="delivery-status" data-status={status}>
      <Icon className="size-3.5" aria-hidden />
      {t(status)}
    </Badge>
  );
}

/** Scope chips: the code in monospace, and a warning icon for personal data. */
export function ScopeChips({ scopes }: { scopes: readonly string[] }) {
  const t = useTranslations("integrations.scopes");
  return (
    <span className="flex flex-wrap gap-1.5" data-testid="key-scopes">
      {scopes.map((scope) => (
        <Badge key={scope} variant={scope === "pii:read" ? "warning" : "outline"} data-scope={scope} title={isKnownScope(scope) ? t(`${scopeKey(scope)}.title`) : undefined}>
          {scope === "pii:read" ? <TriangleAlert className="size-3.5" aria-hidden /> : null}
          <span className="font-mono">{scope}</span>
        </Badge>
      ))}
    </span>
  );
}

export function EventChips({ events }: { events: readonly string[] }) {
  return (
    <span className="flex flex-wrap gap-1.5" data-testid="hook-events">
      {events.map((event) => (
        <Badge key={event} variant="outline" data-event={event}>
          <span className="font-mono">{event}</span>
        </Badge>
      ))}
    </span>
  );
}

/** Plain-words name of an event, or the code itself for one this screen does not know. */
export function useEventLabel() {
  const t = useTranslations("integrations.events");
  return (event: string) => (isKnownEvent(event) ? t(`${eventKey(event)}.title`) : event);
}

/**
 * Copies text to the clipboard and says so, in words and with an icon, for sighted and screen reader users (a polite live region).
 * It is a real button, so Enter and Space work. When the browser refuses the clipboard the button says so and the text can still be
 * selected by hand (the caller shows it in a read-only field).
 */
export function CopyButton({ text, label, copiedLabel, failedLabel, testId = "copy" }: { text: string; label: string; copiedLabel: string; failedLabel: string; testId?: string }) {
  const [state, setState] = React.useState<"idle" | "copied" | "failed">("idle");
  const timer = React.useRef<number | undefined>(undefined);
  React.useEffect(() => () => window.clearTimeout(timer.current), []);
  const copy = async () => {
    window.clearTimeout(timer.current);
    try {
      await navigator.clipboard.writeText(text);
      setState("copied");
    } catch {
      setState("failed");
    }
    timer.current = window.setTimeout(() => setState("idle"), 4000);
  };
  return (
    <>
      <Button type="button" variant="secondary" onClick={() => void copy()} data-testid={testId} data-state={state}>
        {state === "copied" ? <Check aria-hidden /> : <Copy aria-hidden />}
        {state === "copied" ? copiedLabel : state === "failed" ? failedLabel : label}
      </Button>
      <span className="sr-only" role="status" aria-live="polite">
        {state === "copied" ? copiedLabel : state === "failed" ? failedLabel : ""}
      </span>
    </>
  );
}

/** A code sample that wraps instead of scrolling sideways, with a copy button. Samples hold placeholders only. */
export function CodeBlock({ code, label, testId }: { code: string; label: string; testId: string }) {
  const t = useTranslations("integrations.common");
  return (
    <div className="space-y-2">
      <pre className="whitespace-pre-wrap rounded-control border border-line bg-sunken p-3 font-mono text-sm text-strong [overflow-wrap:anywhere]" aria-label={label} data-testid={testId}>
        <code>{code}</code>
      </pre>
      <CopyButton text={code} label={t("copyCode")} copiedLabel={t("copiedCode")} failedLabel={t("copyFailed")} testId={`${testId}-copy`} />
    </div>
  );
}
