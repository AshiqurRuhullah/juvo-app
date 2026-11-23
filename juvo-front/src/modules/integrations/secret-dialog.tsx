"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ShieldAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";
import { CopyButton } from "./common";

/** A secret the API has just handed over. It lives only in the state of the screen that asked for it and is dropped when this dialog closes. */
export type OneTimeSecret = { kind: "key" | "webhook"; value: string; name: string; reason: "created" | "rotated" };

/**
 * The one-time secret dialog (API key or webhook signing secret). The API never shows the secret again, so:
 * - clicking outside the dialog and pressing Escape do nothing (an accidental click must not lose it); the person closes it with the
 *   "I have saved it" button (or the close icon);
 * - the secret is held in component state only: never in the address, the query cache, local storage, logs or error texts;
 * - the Copy button gives feedback in words and works with the keyboard; the value sits in a read-only field so it can also be selected by hand.
 * After closing, focus goes back to `returnFocusTo` (the button that started the flow), because the dialog that started it is gone.
 */
export function SecretDialog({ secret, onDone, returnFocusTo }: { secret: OneTimeSecret | null; onDone: () => void; returnFocusTo: string }) {
  const tc = useTranslations("integrations.common");
  return (
    <Dialog open={secret !== null} onOpenChange={(open) => (open ? undefined : onDone())}>
      <ModalContent
        size="md"
        closeLabel={tc("close")}
        onInteractOutside={(e) => e.preventDefault()}
        onPointerDownOutside={(e) => e.preventDefault()}
        onEscapeKeyDown={(e) => e.preventDefault()}
        onCloseAutoFocus={(e) => {
          const target = document.querySelector<HTMLElement>(returnFocusTo);
          if (target) {
            e.preventDefault();
            target.focus();
          }
        }}
        data-testid="secret-dialog"
      >
        {secret ? <Body secret={secret} onDone={onDone} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function Body({ secret, onDone }: { secret: OneTimeSecret; onDone: () => void }) {
  const t = useTranslations(`integrations.secret.${secret.kind}`);
  const ts = useTranslations("integrations.secret");
  return (
    <>
      <DialogTitle className="font-display text-xl font-semibold text-strong">{t(`${secret.reason}Title`, { name: secret.name })}</DialogTitle>
      <DialogDescription className="mt-2 text-muted">{t(`${secret.reason}Description`)}</DialogDescription>
      <Alert variant="warning" className="mt-4" data-testid="secret-notice">
        <span className="flex items-start gap-2">
          <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
          <span>{ts("notice")}</span>
        </span>
      </Alert>
      <div className="mt-4 space-y-2">
        <label htmlFor="one-time-secret" className="text-sm font-medium text-strong">
          {t("fieldLabel")}
        </label>
        <Input
          id="one-time-secret"
          readOnly
          value={secret.value}
          onFocus={(e) => e.currentTarget.select()}
          className="font-mono text-sm"
          spellCheck={false}
          autoComplete="off"
          data-testid="secret-value"
          data-1p-ignore="true"
          data-lpignore="true"
        />
      </div>
      <div className="mt-6 flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" onClick={onDone} data-testid="secret-done">
          {ts("done")}
        </Button>
        <CopyButton text={secret.value} label={ts("copy")} copiedLabel={ts("copied")} failedLabel={ts("copyFailed")} testId="secret-copy" />
      </div>
    </>
  );
}
