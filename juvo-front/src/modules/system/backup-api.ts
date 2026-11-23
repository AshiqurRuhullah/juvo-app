"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { REFRESH_MS } from "./logic";
import { anyRunning, pollInterval } from "./backup-logic";

/** Typed access to backups and the data inventory (docs/backup-api.md, FR-AF03). Every call goes through the BFF. There is no restore and no download call: the API has none. */
export type BackupRun = Schemas["BackupRunDto"];
export type BackupStatus = Schemas["BackupStatusDto"];
export type DataInventory = Schemas["DataInventoryDto"];

const KEY = ["system", "backups"] as const;

const tabVisible = () => typeof document === "undefined" || document.visibilityState !== "hidden";

/** Status: every 15 s while a backup runs, every 60 s otherwise; no timer while the tab is hidden (it refetches when shown again). */
export function useBackupStatus() {
  return useQuery({
    queryKey: [...KEY, "status"],
    refetchInterval: (query) => pollInterval(anyRunning(query.state.data, undefined), tabVisible()),
    refetchIntervalInBackground: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/backups/status")),
  });
}

/** Runs, newest first. `running` is the status panel's view of a running backup; a Running row in the list counts too. */
export function useBackupRuns(query: Record<string, string | number>, running: boolean) {
  return useQuery({
    queryKey: [...KEY, "runs", query],
    placeholderData: keepPreviousData,
    refetchInterval: (q) => pollInterval(running || anyRunning(undefined, q.state.data?.items), tabVisible()),
    refetchIntervalInBackground: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/backups", { params: { query: query as never } })),
  });
}

/** `POST /system/backups`: 202 with the run. A 409 SYSTEM.BACKUP_RUNNING arrives as an ApiError the screen maps (backup-logic `startFailure`). */
export function useStartBackup() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/system/backups", { body: {} as never })),
    onSettled: () => queryClient.invalidateQueries({ queryKey: KEY }),
  });
}

/** Counts only, refreshed every 30 s like the health panels. */
export function useDataInventory() {
  return useQuery({
    queryKey: ["system", "data-inventory"],
    refetchInterval: REFRESH_MS * 2,
    refetchIntervalInBackground: false,
    queryFn: async () => unwrap(await api.GET("/api/v1/system/data-inventory")),
  });
}
