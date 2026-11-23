import { sql as rawSql } from "./env";

/** sqlcmd prints its error text on stdout; surface it so a bad seed row explains itself. */
function sql(query: string): string {
  try {
    return rawSql(`SET QUOTED_IDENTIFIER ON; ${query}`);
  } catch (error) {
    const out = (error as { stdout?: string }).stdout;
    throw new Error(`sqlcmd failed: ${out ?? (error as Error).message.slice(0, 200)}`);
  }
}

/**
 * Webhook endpoints and delivery rows written straight into the throw-away e2e database. The API (correctly) refuses to create an
 * endpoint on a loopback or http address, and the e2e API runs the production address guard, so an endpoint that the delivery log and
 * the toggles can be tested on has to be inserted. Its address is a public-looking name that never resolves, so the worker never delivers
 * anything. The signing secret column holds a placeholder (the real one is encrypted by the API and not needed here). Every row carries a per-run tag.
 */
export type IntegrationSeed = {
  tag: string;
  /** Endpoint with deliveries (failed x3, succeeded, pending). */
  hookLog: { id: string; name: string };
  /** Endpoint to switch on and off and to send a test event to. */
  hookToggle: { id: string; name: string };
  /** Endpoint to delete. */
  hookDelete: { id: string; name: string };
};

const tagOf = () => Math.random().toString(36).slice(2, 8);
const guid = () => crypto.randomUUID();

export function seedIntegrations(): IntegrationSeed {
  const tag = tagOf();
  const mk = (suffix: string) => ({ id: guid(), name: `E2E hook ${suffix} ${tag}` });
  const seed: IntegrationSeed = { tag, hookLog: mk("log"), hookToggle: mk("toggle"), hookDelete: mk("delete") };
  const now = "SYSDATETIMEOFFSET()";
  const endpoint = (h: { id: string; name: string }, path: string, events: string) =>
    `(N'${h.id}', N'${h.name}', N'https://e2e-hooks.example.invalid/${path}-${tag}', N'placeholder-not-a-secret', N'${events}', 1, NULL, ${now}, NULL, NULL, NULL)`;
  sql(
    `INSERT INTO WebhookEndpoints (Id, Name, Url, SecretProtected, Events, IsEnabled, SecretRotatedAt, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy) VALUES
      ${endpoint(seed.hookLog, "log", "order.placed,product.updated")},
      ${endpoint(seed.hookToggle, "toggle", "stock.low")},
      ${endpoint(seed.hookDelete, "delete", "product.published")}`,
  );
  // Statuses: 0 pending, 1 succeeded, 2 failed. Newest first by CreatedAt, so the failed ones are the newest.
  const delivery = (eventType: string, attempt: number, status: number, http: string, error: string, minutesAgo: number) => {
    const id = guid();
    return `(N'${id}', N'${guid()}', N'${eventType}', N'${seed.hookLog.id}', N'{}', N'${"0".repeat(64)}', ${attempt}, ${status}, ${http}, ${error === "NULL" ? "NULL" : `N'${error}'`}, ${status === 0 ? `DATEADD(day, 1, ${now})` : now}, DATEADD(minute, -${minutesAgo}, ${now}), ${status === 1 ? `DATEADD(minute, -${minutesAgo}, ${now})` : "NULL"}, 0, DATEADD(minute, -${minutesAgo}, ${now}))`;
  };
  sql(
    `INSERT INTO WebhookDeliveries (Id, EventId, EventType, EndpointId, Payload, PayloadHash, Attempt, Status, HttpStatus, Error, NextAttemptAt, LastAttemptAt, DeliveredAt, Redeliveries, CreatedAt) VALUES
      ${delivery("order.placed", 5, 2, "503", "HTTP 503", 1)},
      ${delivery("order.placed", 5, 2, "NULL", "No answer within 5 seconds.", 2)},
      ${delivery("product.updated", 5, 2, "NULL", "The connection failed.", 3)},
      ${delivery("product.updated", 1, 1, "200", "NULL", 4)},
      ${delivery("order.placed", 0, 0, "NULL", "NULL", 5)}`,
  );
  return seed;
}

export function cleanIntegrations(seed: IntegrationSeed) {
  // Deliveries go with their endpoint (cascade).
  sql(`DELETE FROM WebhookEndpoints WHERE Name LIKE N'E2E hook % ${seed.tag}'`);
}

/** Removes API keys and endpoints a spec created through the screens (matched by its per-run tag in the name). */
export function cleanByTag(tag: string) {
  sql(`DELETE FROM ApiKeys WHERE Name LIKE N'%${tag}%'`);
  sql(`DELETE FROM WebhookEndpoints WHERE Name LIKE N'%${tag}%'`);
}

export const newTag = tagOf;

/** A key-shaped value built at run time from pieces, so the source never holds anything a secret scanner could mistake for a real key. */
export const looksLikeApiKey = (value: string): boolean => new RegExp(["^jv", "live", "[A-Za-z0-9]{8}", "[A-Za-z0-9_-]{43}$"].join("_")).test(value);
