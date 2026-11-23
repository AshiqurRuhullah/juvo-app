"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Lock, RotateCcw, Save, ShieldAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input, Switch, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useResetSetting, useSaveSetting, type Setting } from "./api";
import { useSettingsProblem, useUiLocale } from "./common";
import {
  asType, canReset, dangerKind, draftOf, editBlock, isDirty, maxLengthOf, maxOf, minOf, parseDraft, paymentMethodOf, sourceKind, titleOf, valueToDraft,
  type DangerKind, type DraftError,
} from "./logic";

/** One setting: its name, value control by type, default, source and the Save and Reset actions. Everything comes from the API entry. */
export function SettingRow({ setting, notice, onNotice, onStale }: { setting: Setting; notice: string | null; onNotice: (key: string, text: string | null) => void; onStale: (key: string) => void }) {
  const t = useTranslations("settings.row");
  const te = useTranslations("settings.errors");
  const locale = useUiLocale();
  const problem = useSettingsProblem();
  const save = useSaveSetting();
  const reset = useResetSetting();
  const type = asType(setting.type);
  const block = editBlock(setting);
  const [draft, setDraft] = React.useState(() => draftOf(setting));
  const [failure, setFailure] = React.useState<string | null>(null);
  const [confirm, setConfirm] = React.useState<{ kind: DangerKind; value: boolean | number | string } | null>(null);
  const [askReset, setAskReset] = React.useState(false);
  const id = `set-${setting.key}`;
  const dirty = isDirty(setting, draft);
  const title = titleOf(setting, locale);

  const showValue = (value: unknown): string => {
    if (typeof value === "boolean") return value ? t("on") : t("off");
    const text = valueToDraft(value);
    return text === "" ? t("empty") : text;
  };

  const send = async (value: boolean | number | string) => {
    setFailure(null);
    try {
      await save.mutateAsync({ key: setting.key, value, version: setting.version });
      onNotice(setting.key, null);
      setConfirm(null);
      toast.success(t("saved", { name: title }));
    } catch (e) {
      setConfirm(null);
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.code === "GENERAL.CONCURRENCY_CONFLICT") onStale(setting.key);
      else setFailure(problem(p));
    }
  };

  const onSave = () => {
    const parsed = parseDraft(setting, draft);
    if (!parsed.ok) {
      setFailure(te(parsed.error satisfies DraftError, { min: minOf(setting) ?? "", max: maxOf(setting) ?? "", length: maxLengthOf(setting) ?? "" }));
      return;
    }
    setFailure(null);
    const kind = dangerKind(setting.key, parsed.value);
    if (kind) setConfirm({ kind, value: parsed.value });
    else void send(parsed.value);
  };

  const doReset = async () => {
    setFailure(null);
    try {
      await reset.mutateAsync(setting.key);
      onNotice(setting.key, null);
      setAskReset(false);
      toast.success(t("wasReset", { name: title }));
    } catch (e) {
      setAskReset(false);
      const p = e instanceof ApiError ? e.problem : null;
      if (p?.code === "GENERAL.CONCURRENCY_CONFLICT") onStale(setting.key);
      else setFailure(problem(p));
    }
  };

  const disabled = block !== null;
  const describedBy = failure ? `${id}-error` : `${id}-hint`;
  const lo = minOf(setting);
  const hi = maxOf(setting);
  const limit = maxLengthOf(setting);
  const kind = sourceKind(setting.source);

  return (
    <li className="rounded-card border border-line bg-card p-4 sm:p-5" data-testid="setting-row" data-key={setting.key} data-source={setting.source} data-dirty={dirty ? "true" : "false"}>
      <div className="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
        <div className="min-w-0 flex-1">
          <label htmlFor={id} className="block font-semibold text-strong [overflow-wrap:anywhere]" data-testid="setting-title">
            {title}
          </label>
          <p className="font-mono text-sm text-muted [overflow-wrap:anywhere]" data-testid="setting-key">
            {setting.key}
          </p>
        </div>
        <Badge variant={kind === "changed" ? "info" : "outline"} data-testid="setting-source">
          {kind === "config" ? <Lock className="size-3.5" aria-hidden /> : null}
          {t(`source.${kind}`)}
        </Badge>
      </div>

      <div className="mt-3 max-w-xl">
        {type === "bool" ? (
          <div className="flex min-h-11 items-center gap-3">
            <Switch id={id} checked={draft === "true"} onCheckedChange={(on) => setDraft(on ? "true" : "false")} disabled={disabled} aria-describedby={describedBy} data-testid="setting-input" />
            <span className="text-strong" aria-hidden>
              {draft === "true" ? t("on") : t("off")}
            </span>
          </div>
        ) : type === "choice" ? (
          <Select
            id={id}
            aria-label={title}
            value={draft}
            onValueChange={setDraft}
            options={(setting.allowedValues ?? []).map((v) => ({ value: v, label: v }))}
            placeholder={t("choose")}
            searchPlaceholder={t("search")}
            emptyText={t("noMatch")}
            disabled={disabled}
          />
        ) : type === "text" && (limit ?? 0) > 200 ? (
          <Textarea id={id} value={draft} rows={4} maxLength={limit ?? undefined} onChange={(e) => setDraft(e.target.value)} disabled={disabled} aria-describedby={describedBy} data-testid="setting-input" />
        ) : type === "text" ? (
          <Input id={id} value={draft} maxLength={limit ?? undefined} onChange={(e) => setDraft(e.target.value)} disabled={disabled} aria-describedby={describedBy} data-testid="setting-input" />
        ) : (
          <Input
            id={id}
            type="number"
            inputMode={type === "whole" ? "numeric" : "decimal"}
            step={type === "whole" ? 1 : "any"}
            min={lo ?? undefined}
            max={hi ?? undefined}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            disabled={disabled}
            aria-describedby={describedBy}
            data-testid="setting-input"
          />
        )}
      </div>

      <p id={`${id}-hint`} className="mt-2 text-sm text-muted [overflow-wrap:anywhere]" data-testid="setting-meta">
        {t("defaultIs", { value: showValue(setting.default) })}
        {(type === "whole" || type === "number") && (lo !== null || hi !== null) ? ` · ${lo !== null && hi !== null ? t("range", { min: lo, max: hi }) : lo !== null ? t("rangeMin", { min: lo }) : t("rangeMax", { max: hi ?? "" })}` : ""}
        {type === "text" && limit !== null ? ` · ${t("maxLength", { length: limit })}` : ""}
      </p>

      {setting.requiresRestart ? (
        <p className="mt-1 text-sm font-medium text-warning-ink" data-testid="setting-restart">
          {t("restart")}
        </p>
      ) : null}
      {block ? (
        <p className="mt-2 flex items-start gap-2 text-sm text-muted" data-testid="setting-blocked" data-reason={block}>
          <Lock className="mt-0.5 size-4 shrink-0" aria-hidden />
          <span>{block === "noPermission" ? t("blocked.noPermission", { permission: setting.editPermission ?? "" }) : t(`blocked.${block}`)}</span>
        </p>
      ) : null}
      {notice ? (
        <Alert variant="warning" className="mt-3" data-testid="setting-stale">
          {notice}
        </Alert>
      ) : null}
      {failure ? (
        <Alert variant="danger" className="mt-3" id={`${id}-error`} data-testid="setting-error">
          {failure}
        </Alert>
      ) : null}

      {!block ? (
        <div className="mt-4 flex flex-col gap-2 sm:flex-row">
          <Button onClick={onSave} disabled={!dirty} loading={save.isPending} data-testid="setting-save">
            <Save className="size-4" aria-hidden />
            {t("save")}
          </Button>
          {dirty ? (
            <Button variant="secondary" onClick={() => { setDraft(draftOf(setting)); setFailure(null); }} disabled={save.isPending} data-testid="setting-undo">
              {t("undo")}
            </Button>
          ) : null}
          {canReset(setting) ? (
            <Button variant="secondary" onClick={() => setAskReset(true)} disabled={save.isPending || reset.isPending} data-testid="setting-reset">
              <RotateCcw className="size-4" aria-hidden />
              {t("reset")}
            </Button>
          ) : null}
        </div>
      ) : null}

      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => (open ? undefined : setConfirm(null))}
        title={confirm ? t(`danger.${confirm.kind}.title`, { method: paymentMethodOf(setting.key) }) : ""}
        description={confirm ? t(`danger.${confirm.kind}.description`, { method: paymentMethodOf(setting.key), value: String(confirm.value) }) : ""}
        confirmLabel={confirm ? t(`danger.${confirm.kind}.confirm`) : ""}
        cancelLabel={t("cancel")}
        closeLabel={t("close")}
        destructive
        loading={save.isPending}
        onConfirm={() => (confirm ? send(confirm.value) : undefined)}
      >
        <p className="flex items-start gap-2 text-sm font-medium text-danger">
          <ShieldAlert className="mt-0.5 size-4 shrink-0" aria-hidden />
          <span>{t("danger.warning")}</span>
        </p>
      </ConfirmDialog>
      <ConfirmDialog
        open={askReset}
        onOpenChange={setAskReset}
        title={t("resetConfirm.title")}
        description={t("resetConfirm.description", { name: title, default: showValue(setting.default), current: showValue(setting.value) })}
        confirmLabel={t("resetConfirm.confirm")}
        cancelLabel={t("cancel")}
        closeLabel={t("close")}
        loading={reset.isPending}
        onConfirm={() => doReset()}
      />
    </li>
  );
}
