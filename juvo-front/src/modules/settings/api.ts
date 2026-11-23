"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";

/** Typed access to the settings centre (docs/settings-api.md). Every call goes through the BFF. */
export type Setting = Schemas["SettingDto"];
export type SettingChange = Schemas["SettingChangeDto"];

const KEY = ["settings"] as const;
/** How many recent changes the panel asks for (the API allows 1 to 200). */
export const HISTORY_LIMIT = 30;

export function useSettings() {
  return useQuery({
    queryKey: [...KEY, "list"],
    queryFn: async () => unwrap(await api.GET("/api/v1/settings")).groups,
  });
}

export function useSettingsHistory() {
  return useQuery({
    queryKey: [...KEY, "history"],
    queryFn: async () => unwrap(await api.GET("/api/v1/settings/history", { params: { query: { limit: HISTORY_LIMIT } } })),
  });
}

function useInvalidate() {
  const queryClient = useQueryClient();
  return () => queryClient.invalidateQueries({ queryKey: KEY });
}

/** Saves one value. `version` is the row version the screen read (optimistic concurrency); null when the setting has no stored row yet. */
export function useSaveSetting() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (args: { key: string; value: boolean | number | string; version: string | null }) =>
      unwrap(await api.PUT("/api/v1/settings/{key}", { params: { path: { key: args.key } }, body: { value: args.value as never, version: args.version } })),
    onSuccess: invalidate,
  });
}

export function useResetSetting() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: async (key: string) => unwrap(await api.DELETE("/api/v1/settings/{key}", { params: { path: { key } } })),
    onSuccess: invalidate,
  });
}

/** Reloads the list and the history (after a stale-version answer). */
export function useReloadSettings() {
  return useInvalidate();
}
