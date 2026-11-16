import { expect, type Download, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { loadState } from "./env";
import { call } from "./cod";
import { adminAccess } from "./returns";
import { today } from "./ops";

/**
 * Helpers of the report specs: report reads straight from the API (the "before" numbers and the cross-checks), staff with chosen
 * permissions in one chosen branch, and the file download of the export menu.
 */
type Json = Record<string, unknown>;

/** The range every report spec uses: yesterday and today (Bangladesh days), so a late-night run is still inside it. */
export function range(): { from: string; to: string } {
  const to = today();
  const from = new Date(Date.parse(to + "T00:00:00Z") - 86_400_000).toISOString().slice(0, 10);
  return { from, to };
}

export const rangeQuery = (extra = "") => {
  const r = range();
  return `from=${r.from}&to=${r.to}${extra ? "&" + extra : ""}`;
};

/** The address of a report page with the range in the address bar and no branch (consolidated). */
export const reportPath = (report: string, extra = "") => `/admin/reports/${report}?${rangeQuery("branch=" + (extra ? "&" + extra : ""))}`;

/** A report straight from the API as Super Admin (or the given token). */
export async function readReport<T = Json>(path: string, token?: string): Promise<T> {
  const result = await call<T>("GET", `/api/v1/${path}`, { token: token ?? (await adminAccess()), expect: 200 });
  return result.json;
}

export const n = (value: unknown) => Number(value ?? 0);

/** A staff user with a role holding exactly these permissions, assigned to one branch (the head office unless told). */
export async function staffInBranch(permissions: string[], branchId = loadState().branches.ho.id): Promise<{ email: string; password: string }> {
  const admin = await adminAccess();
  const suffix = Math.random().toString(36).slice(2, 8);
  const role = await call<{ id: string }>("POST", "/api/v1/ums/roles", { token: admin, body: { name: `Reports ${suffix}`, description: "reports e2e", permissions }, expect: 201 });
  const email = `rep-${suffix}@e2e.test`;
  const created = await call<{ user: { id: string }; temporaryPassword: string }>("POST", "/api/v1/ums/users", {
    token: admin,
    body: { email, fullName: `Reports ${suffix}`, assignments: [{ branchId, roleId: role.json.id }] },
    expect: 201,
  });
  const limited = await call<{ accessToken: string }>("POST", "/api/v1/auth/login", { body: { identifier: email, password: created.json.temporaryPassword }, expect: 200 });
  const password = "E2e-Staff#Pass1";
  await call("POST", "/api/v1/auth/change-password", { token: limited.json.accessToken, body: { currentPassword: created.json.temporaryPassword, newPassword: password }, expect: 200 });
  return { email, password };
}

/** The subtotal (gross sales) and grand total of an order, from the staff API. */
export async function orderMoney(orderId: string): Promise<{ subtotal: number; grandTotal: number }> {
  const order = await call<{ money: { subtotal: number | string; grandTotal: number | string } }>("GET", `/api/v1/orders/${orderId}`, { token: await adminAccess(), expect: 200 });
  return { subtotal: Number(order.json.money.subtotal), grandTotal: Number(order.json.money.grandTotal) };
}

/** Reads a downloaded file: its suggested name and its bytes. */
export async function readDownload(download: Download): Promise<{ name: string; bytes: Buffer }> {
  const path = await download.path();
  return { name: download.suggestedFilename(), bytes: readFileSync(path) };
}

/** Opens the export menu of a report page, picks a format (and a section) and returns the download. */
export async function exportFrom(page: Page, format: "csv" | "xlsx" | "pdf", section?: string) {
  await page.getByTestId("export-open").click();
  await page.getByTestId(`export-format-${format}`).click();
  if (section) {
    await page.locator("#ex-section").click();
    await page.getByRole("option", { name: section }).click();
  }
  const waiting = page.waitForEvent("download");
  await page.getByTestId("export-download").click();
  return readDownload(await waiting);
}

/** The text of a CSV without the byte order mark, as lines. */
export function csvLines(bytes: Buffer): string[] {
  return bytes.toString("utf8").replace(/^﻿/, "").split(/\r?\n/).filter((l) => l.length > 0);
}

export const startsWith = (bytes: Buffer, text: string) => bytes.subarray(0, text.length).toString("latin1") === text;

/** Money or a count as the screens draw it, by digits only. */
export const digits = (text: string | null) => Number((text ?? "").replace(/[^0-9.-]/g, ""));

export async function tile(page: Page, key: string): Promise<number> {
  const text = await page.getByTestId(`tile-${key}`).first().textContent();
  return digits(text);
}

export { expect };
