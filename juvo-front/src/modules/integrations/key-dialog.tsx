"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { ShieldAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox, ChoiceRow, Field, Input } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Select } from "@/components/ui/select";
import { Alert } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useBranchOptions } from "@/modules/inventory/common";
import { useSaveKey, useScopes, type ApiKey } from "./api";
import { useIntegrationProblem } from "./common";
import {
  API_SCOPES, PII_SCOPE, RATE_MAX, RATE_MIN, dhakaToday, emptyKeyForm, hasErrors, isKnownScope, keyToForm, scopeKey, keyToRequest, needsPiiWarning, orderScopes, toggleScope, validateKey,
  type KeyErrors, type KeyForm,
} from "./logic";
import type { OneTimeSecret } from "./secret-dialog";

const ALL_BRANCHES = "__all";

/** Create or edit one API key. A new key's secret is handed to `onSecret` (the one-time dialog); the form never keeps it. */
export function KeyDialog({ target, onClose, onSecret }: { target: ApiKey | "new" | null; onClose: () => void; onSecret: (secret: OneTimeSecret) => void }) {
  const t = useTranslations("integrations.keyForm");
  const tc = useTranslations("integrations.common");
  return (
    <Dialog open={target !== null} onOpenChange={(open) => (open ? undefined : onClose())}>
      <ModalContent size="lg" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{target === null || target === "new" ? t("newTitle") : t("editTitle", { name: target.name })}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{target === null || target === "new" ? t("newDescription") : t("editDescription")}</DialogDescription>
        {target !== null ? <KeyBody apiKey={target === "new" ? null : target} close={onClose} onSecret={onSecret} /> : null}
      </ModalContent>
    </Dialog>
  );
}

function KeyBody({ apiKey, close, onSecret }: { apiKey: ApiKey | null; close: () => void; onSecret: (secret: OneTimeSecret) => void }) {
  const t = useTranslations("integrations.keyForm");
  const te = useTranslations("integrations.keyForm.errors");
  const ts = useTranslations("integrations.scopes");
  const tc = useTranslations("integrations.common");
  const problem = useIntegrationProblem();
  const branches = useBranchOptions();
  const scopesQuery = useScopes();
  const save = useSaveKey();
  const isNew = !apiKey;
  const [values, setValues] = React.useState<KeyForm>(() => (apiKey ? keyToForm(apiKey) : emptyKeyForm()));
  const [errors, setErrors] = React.useState<KeyErrors>({});
  const [failure, setFailure] = React.useState<string | null>(null);
  const set = <K extends keyof KeyForm>(key: K, value: KeyForm[K]) => setValues((old) => ({ ...old, [key]: value }));
  const err = (key: keyof KeyErrors) => (errors[key] ? te(errors[key]!) : undefined);

  // The API's list decides which scopes exist; until it answers, the documented fixed list is shown so the form is usable at once.
  const scopeList = React.useMemo(() => orderScopes(scopesQuery.data ?? API_SCOPES.map((scope) => ({ scope, description: "", sensitive: scope === PII_SCOPE }))), [scopesQuery.data]);
  const known = React.useMemo(() => scopeList.map((s) => s.scope), [scopeList]);
  // A branch the person cannot see in the list (an older key) is kept as a choice so saving does not silently change it.
  const branchOptions = React.useMemo(() => {
    const options = [{ value: ALL_BRANCHES, label: t("allBranches") }, ...branches];
    if (apiKey?.branchId && !branches.some((b) => b.value === apiKey.branchId)) options.push({ value: apiKey.branchId, label: apiKey.branchCode ?? apiKey.branchId });
    return options;
  }, [branches, apiKey, t]);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const found = validateKey(values, known);
    setErrors(found);
    setFailure(null);
    if (hasErrors(found)) return;
    try {
      const answer = await save.mutateAsync({ id: apiKey?.id, body: keyToRequest(values) });
      if (isNew && answer.secret) {
        onSecret({ kind: "key", value: answer.secret, name: answer.key.name, reason: "created" });
      } else {
        toast.success(t("saved", { name: answer.key.name }));
      }
      close();
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-5" data-testid="key-form">
      <Field label={t("name")} htmlFor="key-name" required hint={t("nameHint")} error={err("name")}>
        <Input id="key-name" value={values.name} maxLength={100} onChange={(e) => set("name", e.target.value)} aria-invalid={!!errors.name} autoComplete="off" />
      </Field>

      <fieldset className="space-y-1" aria-describedby={errors.scopes ? "key-scopes-error" : undefined}>
        <legend className="text-sm font-medium text-strong">
          {t("scopes")}
          <span className="ms-0.5 text-danger" aria-hidden>
            *
          </span>
        </legend>
        <p className="text-sm text-muted">{t("scopesHint")}</p>
        <div className="grid gap-1">
          {scopeList.map(({ scope, description }) => {
            const id = `key-scope-${scope.replace(":", "-")}`;
            const isPii = scope === PII_SCOPE;
            return (
              <ChoiceRow key={scope} htmlFor={id} className="items-start py-2">
                <Checkbox id={id} checked={values.scopes.includes(scope)} onCheckedChange={(v) => set("scopes", toggleScope(values.scopes, scope, v === true))} className="mt-0.5" data-testid={`scope-${scope}`} />
                <span>
                  <span className="block font-mono text-sm font-semibold">{scope}</span>
                  <span className="block text-sm text-muted">{isKnownScope(scope) ? ts(`${scopeKey(scope)}.description`) : description}</span>
                  {isPii ? <span className="block text-sm font-medium text-warning-ink">{ts("pii_read.caution")}</span> : null}
                </span>
              </ChoiceRow>
            );
          })}
        </div>
        {errors.scopes ? (
          <p id="key-scopes-error" role="alert" className="text-sm font-medium text-danger">
            {err("scopes")}
          </p>
        ) : null}
      </fieldset>

      {needsPiiWarning(values.scopes) ? (
        <Alert variant="warning" data-testid="pii-warning">
          <span className="flex items-start gap-2">
            <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
            <span>{t("piiWarning")}</span>
          </span>
        </Alert>
      ) : null}

      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("branch")} htmlFor="key-branch" hint={t("branchHint")}>
          <Select
            id="key-branch"
            value={values.branchId || ALL_BRANCHES}
            onValueChange={(v) => set("branchId", v === ALL_BRANCHES ? "" : v)}
            options={branchOptions}
            placeholder={t("allBranches")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
        </Field>
        <Field label={t("expires")} htmlFor="key-expires" hint={t("expiresHint")} error={err("expiresOn")}>
          <Input id="key-expires" type="date" value={values.expiresOn} min={dhakaToday()} onChange={(e) => set("expiresOn", e.target.value)} aria-invalid={!!errors.expiresOn} />
        </Field>
        <Field label={t("rate")} htmlFor="key-rate" hint={t("rateHint", { min: RATE_MIN, max: RATE_MAX })} error={err("rateLimit")} className="sm:col-span-2">
          <Input id="key-rate" inputMode="numeric" value={values.rateLimit} maxLength={6} onChange={(e) => set("rateLimit", e.target.value.replace(/\D/g, ""))} aria-invalid={!!errors.rateLimit} autoComplete="off" placeholder={t("ratePlaceholder")} />
        </Field>
      </div>

      {failure ? (
        <Alert variant="danger" data-testid="key-error">
          {failure}
        </Alert>
      ) : null}

      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <DialogClose asChild>
          <Button type="button" variant="secondary" disabled={save.isPending}>
            {tc("cancel")}
          </Button>
        </DialogClose>
        <Button type="submit" loading={save.isPending} data-testid="key-save">
          {isNew ? t("create") : t("save")}
        </Button>
      </div>
    </form>
  );
}
