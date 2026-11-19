"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Input, Switch } from "@/components/ui/form";
import { Alert, Badge, Card, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useBadges, useSaveBadge, type TrustBadge } from "./api";
import { FailureAlert, ReadOnlyNote, useContentProblem, useContentRights } from "./common";
import { BADGE_KEYS, hasErrors, validateBadge, type BadgeForm } from "./logic";

/** The four fixed trust badges shown on the home page: text in English and Bengali, on or off. */
export function BadgesEditor() {
  const t = useTranslations("content.badges");
  const rights = useContentRights();
  const problem = useContentProblem();
  const badges = useBadges();
  if (badges.isPending) return <Skeleton className="h-96 w-full" aria-busy="true" />;
  if (badges.isError) return <Alert variant="danger">{problem(badges.error instanceof ApiError ? badges.error.problem : null).text}</Alert>;
  // The four keys are fixed; show them in that order, with the API's order only as a tiebreak.
  const ordered = [...badges.data].sort((a, b) => BADGE_KEYS.indexOf(a.key as (typeof BADGE_KEYS)[number]) - BADGE_KEYS.indexOf(b.key as (typeof BADGE_KEYS)[number]));
  return (
    <div className="space-y-4">
      <p className="max-w-3xl text-muted">{t("subtitle")}</p>
      {!rights.badgeEdit ? <ReadOnlyNote testId="badges-read-only">{t("readOnly")}</ReadOnlyNote> : null}
      <ul className="grid gap-3" data-testid="badges-list">
        {ordered.map((badge) => (
          <li key={badge.key}>
            <BadgeRow key={`${badge.key}-${badge.updatedAt}`} badge={badge} canEdit={rights.badgeEdit} />
          </li>
        ))}
      </ul>
    </div>
  );
}

function BadgeRow({ badge, canEdit }: { badge: TrustBadge; canEdit: boolean }) {
  const t = useTranslations("content.badges");
  const tk = useTranslations("content.badgeNames");
  const te = useTranslations("content.badges.errors");
  const problem = useContentProblem();
  const save = useSaveBadge(badge.key);
  const saved: BadgeForm = { textEn: badge.textEn ?? "", textBn: badge.textBn ?? "", isEnabled: badge.isEnabled };
  const [form, setForm] = React.useState<BadgeForm>(saved);
  const [errors, setErrors] = React.useState<ReturnType<typeof validateBadge>>({});
  const [failure, setFailure] = React.useState<{ text: string; lines: string[] } | null>(null);
  const dirty = JSON.stringify(form) !== JSON.stringify(saved);
  const known = (BADGE_KEYS as readonly string[]).includes(badge.key);
  const name = known ? tk(badge.key) : badge.key;
  const set = <K extends keyof BadgeForm>(key: K, value: BadgeForm[K]) => setForm((old) => ({ ...old, [key]: value }));

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const found = validateBadge(form);
    setErrors(found);
    if (hasErrors(found)) return;
    try {
      await save.mutateAsync({ textEn: form.textEn.trim() || null, textBn: form.textBn.trim() || null, isEnabled: form.isEnabled, sortOrder: Number(badge.sortOrder) });
      toast.success(t("saved", { name }));
    } catch (e) {
      setFailure(problem(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <Card className="p-4" data-testid={`badge-row-${badge.key}`}>
      <form onSubmit={(e) => void submit(e)} noValidate className="space-y-3">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="font-sans text-base font-semibold text-strong">{name}</h2>
          <Badge variant={badge.isEnabled ? "success" : "outline"} data-testid="badge-state">
            {badge.isEnabled ? t("shown") : t("hidden")}
          </Badge>
        </div>
        <p className="text-sm text-muted">{known ? t(`meaning.${badge.key}`) : null}</p>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label={t("textEn")} htmlFor={`bd-${badge.key}-en`} error={errors.textEn ? te(errors.textEn, { max: 160 }) : undefined}>
            <Input id={`bd-${badge.key}-en`} value={form.textEn} disabled={!canEdit} onChange={(e) => set("textEn", e.target.value)} autoComplete="off" aria-invalid={!!errors.textEn} />
          </Field>
          <Field label={t("textBn")} htmlFor={`bd-${badge.key}-bn`} error={errors.textBn ? te(errors.textBn, { max: 160 }) : undefined}>
            <Input id={`bd-${badge.key}-bn`} lang="bn" value={form.textBn} disabled={!canEdit} onChange={(e) => set("textBn", e.target.value)} autoComplete="off" aria-invalid={!!errors.textBn} />
          </Field>
        </div>
        <ChoiceRow htmlFor={`bd-${badge.key}-on`}>
          <Switch id={`bd-${badge.key}-on`} checked={form.isEnabled} disabled={!canEdit} onCheckedChange={(v) => set("isEnabled", v)} />
          {t("showOnShop")}
        </ChoiceRow>
        <FailureAlert failure={failure} testId={`bd-${badge.key}-error`} />
        {canEdit ? (
          <div className="flex justify-end">
            <Button type="submit" loading={save.isPending} disabled={!dirty} data-testid={`bd-${badge.key}-save`}>
              {t("save")}
              <span className="sr-only"> ({name})</span>
            </Button>
          </div>
        ) : null}
      </form>
    </Card>
  );
}
