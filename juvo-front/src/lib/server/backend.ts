import "server-only";

/** Base URL of the Juvo API (server side only). Never exposed to the browser. */
export function apiBaseUrl(): string {
  return (process.env.API_BASE_URL ?? "http://localhost:5080").replace(/\/+$/, "");
}

/** What the API should know about the real browser behind this server-to-server call. */
export type ClientHints = {
  ip?: string;
  userAgent?: string;
  correlationId?: string;
  deviceName?: string;
};

export function clientHintsFrom(headers: Headers): ClientHints {
  const forwarded = headers.get("x-forwarded-for")?.split(",")[0]?.trim();
  return {
    ip: forwarded || headers.get("x-real-ip") || undefined,
    userAgent: headers.get("user-agent") || undefined,
    correlationId: headers.get("x-correlation-id") || undefined,
    deviceName: headers.get("x-device-name") || undefined,
  };
}

export type BackendInit = Omit<RequestInit, "headers"> & {
  token?: string;
  branch?: string;
  client?: ClientHints;
  headers?: Record<string, string>;
};

/**
 * Calls the API on behalf of the browser. Adds the bearer token and the active branch, and forwards the real client address
 * and user agent (the API trusts X-Forwarded-For only from this server's address).
 */
export async function backendFetch(path: string, init: BackendInit = {}): Promise<Response> {
  const { token, branch, client, headers, ...rest } = init;
  const merged: Record<string, string> = { Accept: "application/json", ...headers };
  if (token) merged.Authorization = `Bearer ${token}`;
  if (branch) merged["X-Branch-Id"] = branch;
  if (client?.ip) merged["X-Forwarded-For"] = client.ip;
  if (client?.userAgent) merged["User-Agent"] = client.userAgent;
  if (client?.correlationId) merged["X-Correlation-Id"] = client.correlationId;
  if (client?.deviceName) merged["X-Device-Name"] = client.deviceName;

  return fetch(`${apiBaseUrl()}${path}`, { ...rest, headers: merged, cache: "no-store", redirect: "manual" });
}

export function jsonInit(body: unknown): Pick<BackendInit, "body" | "headers"> {
  return { body: JSON.stringify(body), headers: { "Content-Type": "application/json" } };
}
