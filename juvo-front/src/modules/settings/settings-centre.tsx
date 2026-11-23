"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { SearchX } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { SearchBox } from "@/components/data/list-kit";
import { cn } from "@/lib/utils";
import { useReloadSettings, useSettings, type Setting } from "./api";
import { useGroupLabel } from "./common";
import { HistoryPanel } from "./history-panel";
import { SettingRow } from "./setting-row";
import { activeGroup, countSettings, filterGroups } from "./logic";

/**
 * `/admin/settings`: every setting the API lists, grouped. Nothing is hard-coded here: names, defaults, limits, who may change what
 * and the read-only entries all come from `GET /settings`. Search looks over all groups at once; without a search one group shows at a time.
 */
export function SettingsCentre() {
  const t = useTranslations("settings");
  const tc = useTranslations("settings.common");
  const groupLabel = useGroupLabel();
  const query = useSettings();
  const reload = useReloadSettings();
  const [searchText, setSearchText] = React.useState("");
  const [wanted, setWanted] = React.useState<string | null>(null);
  const [notices, setNotices] = React.useState<Record<string, string>>({});

  const groups = React.useMemo(() => query.data ?? [], [query.data]);
  const searching = searchText.trim() !== "";
  const matched = React.useMemo(() => filterGroups(groups, searchText), [groups, searchText]);
  const current = activeGroup(groups, wanted);
  const shown = searching ? matched : groups.filter((g) => g.group === current);
  const byKey = React.useMemo(() => new Map<string, Setting>(groups.flatMap((g) => g.settings.map((s) => [s.key, s] as const))), [groups]);

  const setNotice = React.useCallback((key: string, text: string | null) => {
    setNotices((all) => {
      if (text === null) {
        if (!(key in all)) return all;
        const { [key]: _gone, ...rest } = all;
        void _gone;
        return rest;
      }
      return { ...all, [key]: text };
    });
  }, []);
  const onStale = React.useCallback(
    (key: string) => {
      setNotice(key, t("row.stale"));
      void reload();
    },
    [reload, setNotice, t],
  );

  return (
    <div data-testid="settings-centre">
      {query.isPending ? (
        <div className="space-y-3" aria-busy="true" data-testid="settings-loading">
          <Skeleton className="h-12" />
          <Skeleton className="h-28" />
          <Skeleton className="h-28" />
        </div>
      ) : query.isError ? (
        <Alert variant="danger" data-testid="settings-load-error">
          <span>{tc("loadFailed")}</span>{" "}
          <Button variant="secondary" onClick={() => void query.refetch()}>
            {tc("retry")}
          </Button>
        </Alert>
      ) : groups.length === 0 ? (
        <EmptyState title={t("empty.title")} description={t("empty.description")} />
      ) : (
        <>
          <p className="max-w-3xl text-muted">{t("subtitle")}</p>
          <SearchBox className="mt-4 max-w-xl" value={searchText} onChange={setSearchText} placeholder={t("searchPlaceholder")} label={t("search")} />
          {!searching ? (
            <nav aria-label={t("groupsLabel")} className="mt-4 flex flex-wrap gap-2" data-testid="settings-groups">
              {groups.map((g) => (
                <button
                  key={g.group}
                  type="button"
                  aria-current={g.group === current ? "true" : undefined}
                  onClick={() => setWanted(g.group)}
                  data-testid="settings-group"
                  data-group={g.group}
                  className={cn(
                    "inline-flex min-h-11 items-center gap-2 rounded-control border px-3 text-base font-semibold transition-colors",
                    g.group === current ? "border-primary bg-primary-soft text-primary-dark" : "border-line bg-card text-strong [@media(hover:hover)]:hover:border-primary",
                  )}
                >
                  {groupLabel(g.group)}
                  <span className="text-sm font-normal text-muted">{g.settings.length}</span>
                </button>
              ))}
            </nav>
          ) : (
            <p className="mt-4 text-sm text-muted" role="status" data-testid="settings-search-count">
              {t("searchCount", { count: countSettings(matched) })}
            </p>
          )}

          {shown.length === 0 ? (
            <EmptyState
              className="mt-4"
              icon={<SearchX aria-hidden />}
              title={t("noMatch.title")}
              description={t("noMatch.description")}
              action={
                <Button variant="secondary" onClick={() => setSearchText("")}>
                  {tc("clear")}
                </Button>
              }
            />
          ) : (
            shown.map((g) => (
              <section key={g.group} className="mt-6" aria-labelledby={`group-${g.group}`} data-testid="settings-section" data-group={g.group}>
                <h2 id={`group-${g.group}`} className="font-display text-xl font-semibold text-strong">
                  {groupLabel(g.group)}
                </h2>
                <ul className="mt-3 space-y-3">
                  {g.settings.map((s) => (
                    <SettingRow key={`${s.key}:${s.version ?? ""}:${String(s.value)}:${s.isSet}`} setting={s} notice={notices[s.key] ?? null} onNotice={setNotice} onStale={onStale} />
                  ))}
                </ul>
              </section>
            ))
          )}
          <HistoryPanel settings={byKey} />
        </>
      )}
    </div>
  );
}
