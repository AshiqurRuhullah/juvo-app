"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Checkbox, ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useSaveWebhook, type WebhookEndpoint } from "./api";
import { useIntegrationProblem } from "./common";
import { URL_MAX, WEBHOOK_EVENTS, eventKey, emptyWebhookForm, hasErrors, toggleScope, validateWebhook, webhookToForm, webhookToRequest, type WebhookErrors, type WebhookForm } from "./logic";
import type { OneTimeSecret } from "./secret-dialog";

/** Create or edit one webhook endpoint. A new endpoint's signing secret is handed to `onSecret` (the one-time dialog). */
export function WebhookDialog({ target, onClose, onSecret }: { target: WebhookEndpoint | "new" | null; onClose: () => void; onSecret: (secret: OneTimeSecret) => void }) {
  const t = useTranslations("integrations.hookForm");
  const tc = useTranslations("integrations.common");
  return (
    <Dialog open={target !== null} onOpenChange={(open) => (open ? undefined : onClose())}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{target === null || target === "new" ? t("newTitle") : t("editTitle", { name: target.name })}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{target === null || target === "new" ? t("newDescription") : t("editDescription")}</DialogDescription>
        {target !== null ? <Body endpoint={target === "new" ? null : target} close={onClose} onSecret={onSecret} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function Body({ endpoint, close, onSecret }: { endpoint: WebhookEndpoint | null; close: () => void; onSecret: (secret: OneTimeSecret) => void }) {
  const t = useTranslations("integrations.hookForm");
  const te = useTranslations("integrations.hookForm.errors");
  const tev = useTranslations("integrations.events");
  const tc = useTranslations("integrations.common");
  const problem = useIntegrationProblem();
  const save = useSaveWebhook();
  const isNew = !endpoint;
  const [values, setValues] = React.useState<WebhookForm>(() => (endpoint ? webhookToForm(endpoint) : emptyWebhookForm()));
  const [errors, setErrors] = React.useState<WebhookErrors>({});
  // The API's own words when it refuses the address (private network, cannot be resolved ...): shown beside the field, as they come.
  const [urlRefusal, setUrlRefusal] = React.useState<string | null>(null);
  const [failure, setFailure] = React.useState<string | null>(null);
  const set = <K extends keyof WebhookForm>(key: K, value: WebhookForm[K]) => setValues((old) => ({ ...old, [key]: value }));
  const err = (key: keyof WebhookErrors) => (errors[key] ? te(errors[key]!) : undefined);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateWebhook(values);
    setErrors(found);
    setFailure(null);
    setUrlRefusal(null);
    if (hasErrors(found)) return;
    try {
      const answer = await save.mutateAsync({ id: endpoint?.id, body: webhookToRequest(values) });
      if (isNew && answer.secret) onSecret({ kind: "webhook", value: answer.secret, name: answer.endpoint.name, reason: "created" });
      else toast.success(t("saved", { name: answer.endpoint.name }));
      close();
    } catch (e) {
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.code === "INTEGRATIONS.URL_REFUSED") setUrlRefusal(problem(p));
      else setFailure(problem(p));
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-5" data-testid="hook-form">
      <Field label={t("name")} htmlFor="hook-name" required hint={t("nameHint")} error={err("name")}>
        <Input id="hook-name" value={values.name} maxLength={100} onChange={(e) => set("name", e.target.value)} aria-invalid={!!errors.name} autoComplete="off" />
      </Field>
      <Field label={t("url")} htmlFor="hook-url" required hint={t("urlHint")} error={urlRefusal ?? err("url")}>
        <Input
          id="hook-url"
          type="url"
          inputMode="url"
          value={values.url}
          maxLength={URL_MAX + 50}
          onChange={(e) => {
            setUrlRefusal(null);
            set("url", e.target.value);
          }}
          aria-invalid={!!errors.url || !!urlRefusal}
          autoComplete="off"
          autoCapitalize="none"
          spellCheck={false}
          placeholder="https://example.com/juvo-webhook"
          className="font-mono"
        />
      </Field>

      <fieldset className="space-y-1" aria-describedby={errors.events ? "hook-events-error" : undefined}>
        <legend className="text-sm font-medium text-strong">
          {t("events")}
          <span className="ms-0.5 text-danger" aria-hidden>
            *
          </span>
        </legend>
        <p className="text-sm text-muted">{t("eventsHint")}</p>
        <div className="grid gap-1">
          {WEBHOOK_EVENTS.map((event) => {
            const id = `hook-event-${event.replace(/\W/g, "-")}`;
            return (
              <ChoiceRow key={event} htmlFor={id} className="items-start py-2">
                <Checkbox id={id} checked={values.events.includes(event)} onCheckedChange={(v) => set("events", toggleScope(values.events, event, v === true))} className="mt-0.5" data-testid={`event-${event}`} />
                <span>
                  <span className="block font-medium">{tev(`${eventKey(event)}.title`)}</span>
                  <span className="block font-mono text-xs text-muted">{event}</span>
                  <span className="block text-sm text-muted">{tev(`${eventKey(event)}.description`)}</span>
                </span>
              </ChoiceRow>
            );
          })}
        </div>
        {errors.events ? (
          <p id="hook-events-error" role="alert" className="text-sm font-medium text-danger">
            {err("events")}
          </p>
        ) : null}
      </fieldset>

      <ChoiceRow htmlFor="hook-enabled">
        <Switch id="hook-enabled" checked={values.isEnabled} onCheckedChange={(v) => set("isEnabled", v)} data-testid="hook-enabled" />
        <span>
          <span className="block font-medium">{t("enabled")}</span>
          <span className="block text-sm text-muted">{t("enabledHint")}</span>
        </span>
      </ChoiceRow>

      {failure ? (
        <Alert variant="danger" data-testid="hook-error">
          {failure}
        </Alert>
      ) : null}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="hook-save">
          {isNew ? t("create") : t("save")}
        </Button>
      </div>
    </form>
  );
}
