import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";

export const API = "http://localhost:5081";
export const STATE_FILE = resolve(__dirname, "../.state/state.json");

export type Seeded = { email: string; password: string; id: string; name: string };
export type State = {
  db: string;
  admin: Seeded;
  orderManager: Seeded;
  multi: Seeded;
  manager: Seeded;
  branches: { a: { id: string; code: string; name: string }; b: { id: string; code: string; name: string }; ho: { id: string; code: string } };
};

export function saveState(state: State) {
  mkdirSync(dirname(STATE_FILE), { recursive: true });
  writeFileSync(STATE_FILE, JSON.stringify(state, null, 2));
}

export function loadState(): State {
  if (!existsSync(STATE_FILE)) throw new Error("e2e/.state/state.json is missing: the setup project has not run.");
  return JSON.parse(readFileSync(STATE_FILE, "utf8")) as State;
}

/** Runs a T-SQL statement on the e2e database with sqlcmd (Windows authentication) and returns the text output. */
export function sql(query: string, database?: string): string {
  const server = process.env.JUVO_TEST_SQL_SERVER ?? "(localdb)\\MSSQLLocalDB";
  const db = database ?? process.env.E2E_DB!;
  return execFileSync("sqlcmd", ["-S", server, "-E", "-C", "-d", db, "-h", "-1", "-W", "-b", "-Q", `SET NOCOUNT ON; ${query}`], { encoding: "utf8" }).trim();
}

export function dropDatabase() {
  const db = process.env.E2E_DB!;
  if (!db.startsWith("JuvoDb_E2E_")) throw new Error(`Refusing to drop ${db}`);
  sql(`IF DB_ID(N'${db}') IS NOT NULL BEGIN ALTER DATABASE [${db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [${db}]; END`, "master");
}

/** The newest one-time code the sandbox SMS sender wrote for this number (the notification log). */
export function latestOtp(normalizedMobile: string): string {
  const body = sql(`SELECT TOP 1 Body FROM NotificationLogs WHERE Recipient = N'${normalizedMobile}' ORDER BY SentAt DESC`);
  const match = body.match(/\b(\d{6})\b/);
  if (!match) throw new Error(`No OTP found for ${normalizedMobile}: ${body}`);
  return match[1]!;
}

/** Calls the API directly (for preparing data; the tests themselves go through the browser). */
export async function api<T = unknown>(method: string, path: string, body?: unknown, token?: string): Promise<{ status: number; json: T }> {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  return { status: response.status, json: (text ? JSON.parse(text) : undefined) as T };
}
