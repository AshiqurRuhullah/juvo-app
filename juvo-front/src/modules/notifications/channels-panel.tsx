"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { ChoiceRow, Field, Switch } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { LANGUAGES, useChannels, useSaveChannels, type ChannelSettings } from "./api";

/** The form starts from the saved settings; mount it only once they are loaded. */
function ChannelsForm({ initial, canEdit }: { initial: ChannelSettings; canEdit: boolean }) {
  const t = useTranslations("notifications.channels");
  const tl = useTranslations("notifications.language");
  const tc = useTranslations("notifications.common");
  const problemText = useProblemMessage();
  const save = useSaveChannels();
  const [sms, setSms] = React.useState(initial.smsEnabled);
  const [email, setEmail] = React.useState(initial.emailEnabled);
  const [whatsApp, setWhatsApp] = React.useState(initial.whatsAppEnabled);
  const [language, setLanguage] = React.useState(initial.defaultLanguage);
  const dirty = sms !== initial.smsEnabled || email !== initial.emailEnabled || whatsApp !== initial.whatsAppEnabled || language !== initial.defaultLanguage;

  const submit = async () => {
    try {
      await save.mutateAsync({ smsEnabled: sms, emailEnabled: email, whatsAppEnabled: whatsApp, defaultLanguage: language });
      toast.success(t("saved"));
    } catch (failure) {
      toast.error(problemText(failure instanceof ApiError ? failure.problem : null));
    }
  };

  const rows = [
    { id: "ch-sms", key: "sms", value: sms, set: setSms },
    { id: "ch-email", key: "email", value: email, set: setEmail },
    { id: "ch-whatsapp", key: "whatsApp", value: whatsApp, set: setWhatsApp },
  ] as const;

  return (
    <Card className="max-w-2xl space-y-5 p-4 sm:p-5">
      {!canEdit ? <Alert variant="info">{t("readOnly")}</Alert> : null}
      <ul className="space-y-3">
        {rows.map((row) => (
          <li key={row.id} className="rounded-control border border-line bg-card p-3">
            <ChoiceRow htmlFor={row.id} className="justify-between">
              <span className="min-w-0">
                <span className="block font-semibold">{t(`${row.key}.label`)}</span>
                <span className="block text-sm font-normal text-muted">{t(`${row.key}.help`)}</span>
              </span>
              <Switch id={row.id} checked={row.value} onCheckedChange={row.set} disabled={!canEdit} />
            </ChoiceRow>
          </li>
        ))}
      </ul>
      <Field label={t("defaultLanguage")} htmlFor="ch-language" hint={t("defaultLanguageHelp")}>
        <Select id="ch-language" value={language} onValueChange={setLanguage} disabled={!canEdit} options={LANGUAGES.map((l) => ({ value: l, label: tl(l) }))} placeholder={t("defaultLanguage")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
      </Field>
      {canEdit ? (
        <Button onClick={() => void submit()} loading={save.isPending} disabled={!dirty} data-testid="channels-save">
          {t("save")}
        </Button>
      ) : null}
    </Card>
  );
}

export function ChannelsPanel({ canEdit }: { canEdit: boolean }) {
  const tc = useTranslations("notifications.common");
  const query = useChannels();
  if (query.isLoading) return <Skeleton className="h-64" aria-busy />;
  if (query.isError || !query.data) return <Alert variant="danger">{tc("loadFailed")}</Alert>;
  // Key by the saved values so a refetch after saving starts the form from the new state.
  const d = query.data;
  return <ChannelsForm key={`${d.smsEnabled}-${d.emailEnabled}-${d.whatsAppEnabled}-${d.defaultLanguage}`} initial={d} canEdit={canEdit} />;
}
