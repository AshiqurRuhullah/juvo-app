import { call } from "./cod";
import { adminAccess } from "./returns";

/** Helpers for the settings centre specs: read, write and reset one setting on the real API as the admin. */
export type SettingApi = { key: string; value: unknown; default: unknown; source: string; isSet: boolean; version: string | null; canEdit: boolean; readOnly: boolean };

export async function readSetting(key: string): Promise<SettingApi> {
  const token = await adminAccess();
  return (await call<SettingApi>("GET", `/api/v1/settings/${encodeURIComponent(key)}`, { token, expect: 200 })).json;
}

export async function writeSetting(key: string, value: unknown, version?: string | null): Promise<SettingApi> {
  const token = await adminAccess();
  return (await call<SettingApi>("PUT", `/api/v1/settings/${encodeURIComponent(key)}`, { token, body: { value, version: version ?? null }, expect: 200 })).json;
}

/** Puts a setting back to its default (the row is removed). Safe to call when nothing was changed. */
export async function resetSetting(key: string): Promise<void> {
  const token = await adminAccess();
  await call("DELETE", `/api/v1/settings/${encodeURIComponent(key)}`, { token, expect: 200 });
}

/** The row of one setting on the settings screen (works for the table-less list on every width). */
export const settingRow = (page: import("@playwright/test").Page, key: string) => page.locator(`[data-testid="setting-row"][data-key="${key}"]`);
