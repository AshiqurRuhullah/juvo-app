"use client";

import { useTranslations } from "next-intl";
import { History } from "lucide-react";
import { Alert, Skeleton } from "@/components/ui/surfaces";
import { useDateTime } from "@/components/data/list-kit";
import { useSettingsHistory } from "./api";
import { useUiLocale } from "./common";
import { boolOfText, historyKind, titleOf, type SettingLike } from "./logic";

/** "Recent changes": who changed which setting, when, from what to what, newest first (the API masks sensitive values). */
export function HistoryPanel({ settings }: { settings: ReadonlyMap<string, SettingLike> }) {
  const t = useTranslations("settings.history");
  const tr = useTranslations("settings.row");
  const locale = useUiLocale();
  const dateTime = useDateTime();
  const query = useSettingsHistory();

  const show = (text: string | null): string => {
    if (text === null) return tr("empty");
    const bool = boolOfText(text);
    if (bool !== null) return bool ? tr("on") : tr("off");
    return text === "" ? tr("empty") : text;
  };

  return (
    <section aria-labelledby="settings-history-title" className="mt-10" data-testid="settings-history">
      <h2 id="settings-history-title" className="flex items-center gap-2 font-display text-xl font-semibold text-strong">
        <History className="size-5" aria-hidden />
        {t("title")}
      </h2>
      <p className="mt-1 text-sm text-muted">{t("subtitle")}</p>
      <div className="mt-4">
        {query.isPending ? (
          <div className="space-y-2" aria-busy="true">
            <Skeleton className="h-16" />
            <Skeleton className="h-16" />
          </div>
        ) : query.isError ? (
          <Alert variant="danger">{t("loadFailed")}</Alert>
        ) : query.data.length === 0 ? (
          <p className="rounded-card border border-dashed border-line p-6 text-center text-muted" data-testid="history-empty">
            {t("empty")}
          </p>
        ) : (
          <ul className="divide-y divide-line rounded-card border border-line bg-card">
            {query.data.map((h) => {
              const kind = historyKind(h);
              const known = settings.get(h.key);
              return (
                <li key={h.id} className="p-4" data-testid="history-row" data-key={h.key} data-kind={kind}>
                  <p className="font-semibold text-strong [overflow-wrap:anywhere]">{known ? titleOf(known, locale) : h.key}</p>
                  <p className="font-mono text-sm text-muted [overflow-wrap:anywhere]">{h.key}</p>
                  <p className="mt-1 text-strong [overflow-wrap:anywhere]" data-testid="history-change">
                    {kind === "reset" ? t("reset", { old: show(h.oldValue) }) : kind === "first" ? t("first", { value: show(h.newValue) }) : t("changed", { old: show(h.oldValue), value: show(h.newValue) })}
                  </p>
                  <p className="mt-1 text-sm text-muted" data-testid="history-who">
                    {t("by", { who: h.changedByName ?? t("system"), when: dateTime(h.at) })}
                  </p>
                </li>
              );
            })}
          </ul>
        )}
      </div>
    </section>
  );
}
