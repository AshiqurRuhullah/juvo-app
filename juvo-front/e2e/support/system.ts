import { sql as rawSql } from "./env";

/** sqlcmd prints its error text on stdout; surface it so a bad seed row explains itself. */
function sql(query: string): string {
  try {
    return rawSql(`SET QUOTED_IDENTIFIER ON; ${query}`); // the tables have filtered indexes
  } catch (error) {
    const out = (error as { stdout?: string }).stdout;
    throw new Error(`sqlcmd failed: ${out ?? (error as Error).message.slice(0, 200)}`);
  }
}

/**
 * Rows the system screens need, written straight into the throw-away e2e database (the API has no way to create a dead-lettered queue
 * message or a failed send on demand). Every row carries a per-run tag so specs find their own rows and ignore everything else.
 */
export type SystemSeed = {
  tag: string;
  /** Job names (two jobs: one failed run with a secret in its error, one success). */
  jobFailed: string;
  jobOk: string;
  /** Distinct reference of the failed run. */
  runReference: string;
  runCorrelation: string;
  /** Queue message type prefix; rows are `${typePrefix}.dead.N`, `.retry`, `.pending`, `.single`. */
  typePrefix: string;
  /** Template prefix of the failed sends (`${templatePrefix}.sms`, `.mail`, `.sms2`). */
  templatePrefix: string;
  secret: string;
};

export const newTag = () => Math.random().toString(36).slice(2, 8);

export function seedSystem(): SystemSeed {
  const tag = newTag();
  const seed: SystemSeed = {
    tag,
    jobFailed: `e2esys${tag}.failing-job`,
    jobOk: `e2esys${tag}.ok-job`,
    runReference: `ref-${tag}`,
    runCorrelation: `corr-${tag}`,
    typePrefix: `e2esys${tag}`,
    templatePrefix: `e2esys${tag}`,
    secret: `SECRET${tag.toUpperCase()}`,
  };
  const now = "SYSDATETIMEOFFSET()";
  // JobLogs: a success and a failure whose error holds a secret (the API must hide it).
  sql(
    `INSERT INTO JobLogs (Id, JobName, Reference, Attempt, Status, StartedAt, FinishedAt, Error, CorrelationId, CreatedAt) VALUES
      (NEWID(), N'${seed.jobOk}', NULL, 1, 1, DATEADD(minute, -10, ${now}), DATEADD(minute, -10, ${now}), NULL, N'ok-${tag}', ${now}),
      (NEWID(), N'${seed.jobFailed}', N'${seed.runReference}', 2, 2, DATEADD(minute, -5, ${now}), DATEADD(minute, -5, ${now}), N'Gateway refused: api_key=${seed.secret} was rejected', N'${seed.runCorrelation}', ${now})`,
  );
  // Outbox: five dead-lettered (bulk and single tests), one retrying (next try tomorrow), one pending (fresh, not requeueable).
  const dead = (name: string, attempts = 5) =>
    `(NEWID(), N'${seed.typePrefix}.${name}', N'{}', 2, ${attempts}, ${now}, ${now}, N'Handler failed: token=${seed.secret}', N'dead-${tag}', ${now})`;
  sql(
    `INSERT INTO OutboxMessages (Id, Type, Payload, Status, AttemptCount, NextAttemptAt, ProcessedAt, LastError, CorrelationId, CreatedAt) VALUES
      ${["dead.a", "dead.b", "dead.c", "dead.d", "single"].map((n) => dead(n)).join(",\n      ")},
      (NEWID(), N'${seed.typePrefix}.retry', N'{}', 0, 2, DATEADD(day, 1, ${now}), NULL, N'Timeout talking to the handler', N'retry-${tag}', ${now}),
      (NEWID(), N'${seed.typePrefix}.pending', N'{}', 0, 0, DATEADD(day, 1, ${now}), NULL, NULL, N'pending-${tag}', ${now})`,
  );
  // Failed sends (Channel 1 = SMS, 2 = e-mail; Status 2 = Failed, 4 = DeadLetter).
  sql(
    `INSERT INTO NotificationLogs (Id, Channel, Recipient, Template, Subject, Body, Status, ProviderResponse, CorrelationId, SentAt, QueuedAt, Attempts, TemplateVersion, Category, LastError, CreatedAt) VALUES
      (NEWID(), 1, N'+8801700000001', N'${seed.templatePrefix}.sms', NULL, N'private text one', 4, NULL, N'n1-${tag}', ${now}, ${now}, 3, 1, 1, N'Provider said: password=${seed.secret}', ${now}),
      (NEWID(), 2, N'someone.private@example.com', N'${seed.templatePrefix}.mail', N'Subject', N'private text two', 2, NULL, N'n2-${tag}', ${now}, ${now}, 1, 1, 1, N'Mailbox full', ${now}),
      (NEWID(), 1, N'+8801700000002', N'${seed.templatePrefix}.sms2', NULL, N'private text three', 4, NULL, N'n3-${tag}', ${now}, ${now}, 3, 1, 1, N'Carrier rejected', ${now}),
      (NEWID(), 1, N'+8801700000003', N'${seed.templatePrefix}.sms3', NULL, N'private text four', 4, NULL, N'n4-${tag}', ${now}, ${now}, 3, 1, 1, N'Carrier rejected', ${now})`,
  );
  return seed;
}

/** Many dead-lettered rows with one type, to reach the 100-item selection limit (more than 100 rows, ten per page). */
export function seedManyDead(tag: string, count = 105): string {
  const type = `e2esysbulk${tag}`;
  sql(
    `WITH n AS (SELECT TOP (${count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
     INSERT INTO OutboxMessages (Id, Type, Payload, Status, AttemptCount, NextAttemptAt, ProcessedAt, LastError, CorrelationId, CreatedAt)
     SELECT NEWID(), N'${type}.' + CAST(i AS nvarchar(10)), N'{}', 2, 5, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET(), N'bulk failure', N'bulk-${tag}', SYSDATETIMEOFFSET() FROM n`,
  );
  return type;
}

export const outboxStatusOf = (typeLike: string): string => sql(`SELECT Status FROM OutboxMessages WHERE Type = N'${typeLike}'`);
export const outboxAttemptsOf = (typeLike: string): string => sql(`SELECT AttemptCount FROM OutboxMessages WHERE Type = N'${typeLike}'`);
export const markOutbox = (type: string, status: number) => sql(`UPDATE OutboxMessages SET Status = ${status} WHERE Type = N'${type}'`);
export const deleteOutbox = (type: string) => sql(`DELETE FROM OutboxMessages WHERE Type = N'${type}'`);
export const notificationStatusOf = (template: string): string => sql(`SELECT Status FROM NotificationLogs WHERE Template = N'${template}'`);
export const markNotification = (template: string, status: number) => sql(`UPDATE NotificationLogs SET Status = ${status} WHERE Template = N'${template}'`);

/** Removes the rows of a run (the database is dropped after the suite, this keeps reruns against a shared database tidy). */
export function cleanSystem(seed: SystemSeed) {
  sql(`DELETE FROM OutboxMessages WHERE Type LIKE N'e2esys%${seed.tag}%' OR Type LIKE N'${seed.typePrefix}.%'`);
  sql(`DELETE FROM NotificationLogs WHERE Template LIKE N'${seed.templatePrefix}.%'`);
  sql(`DELETE FROM JobLogs WHERE JobName LIKE N'e2esys${seed.tag}.%'`);
}

/** One more dead-lettered message and one more failed send, for specs that must not depend on what earlier specs requeued. */
export function seedFreshRows(seed: SystemSeed, name: string) {
  const now = "SYSDATETIMEOFFSET()";
  sql(`INSERT INTO OutboxMessages (Id, Type, Payload, Status, AttemptCount, NextAttemptAt, ProcessedAt, LastError, CorrelationId, CreatedAt) VALUES (NEWID(), N'${seed.typePrefix}.dead.${name}', N'{}', 2, 5, ${now}, ${now}, N'Handler failed', N'fresh-${seed.tag}', ${now})`);
  sql(
    `INSERT INTO NotificationLogs (Id, Channel, Recipient, Template, Subject, Body, Status, ProviderResponse, CorrelationId, SentAt, QueuedAt, Attempts, TemplateVersion, Category, LastError, CreatedAt) VALUES
      (NEWID(), 1, N'+8801700000009', N'${seed.templatePrefix}.${name}', NULL, N'private text', 4, NULL, N'fresh-${seed.tag}', ${now}, ${now}, 3, 1, 1, N'Carrier rejected', ${now})`,
  );
}
