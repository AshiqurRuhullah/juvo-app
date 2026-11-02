import type { ApiProblem } from "./types";

/** Narrows whatever a failed call returned to the API's problem shape. */
export function toProblem(error: unknown, status?: number): ApiProblem {
  if (error && typeof error === "object") {
    const e = error as Record<string, unknown>;
    return {
      status: typeof e.status === "number" ? e.status : status,
      title: typeof e.title === "string" ? e.title : undefined,
      detail: typeof e.detail === "string" ? e.detail : undefined,
      code: typeof e.code === "string" ? e.code : undefined,
      correlationId: typeof e.correlationId === "string" ? e.correlationId : undefined,
      errors: e.errors && typeof e.errors === "object" ? (e.errors as Record<string, string[]>) : undefined,
    };
  }
  return { status, title: typeof error === "string" ? error : undefined };
}

/** Sign-in problems that mean "start again from the login page". */
export const SESSION_ENDED_CODES = new Set(["AUTH.UNAUTHORIZED", "AUTH.TOKEN_EXPIRED", "AUTH.REFRESH_REUSED", "AUTH.ACCOUNT_DISABLED"]);

export function sessionEnded(problem: ApiProblem): boolean {
  return problem.status === 401 && (problem.code === undefined || SESSION_ENDED_CODES.has(problem.code));
}

/** Field name as the API writes it (PascalCase or camelCase) to the form field name used in our forms. */
export function fieldName(apiName: string): string {
  return apiName.length ? apiName[0]!.toLowerCase() + apiName.slice(1) : apiName;
}

/** Turns the API's validation errors into { fieldName: firstMessage } for React Hook Form. */
export function fieldErrors(problem: ApiProblem): Record<string, string> {
  const result: Record<string, string> = {};
  for (const [name, messages] of Object.entries(problem.errors ?? {})) {
    if (messages[0]) result[fieldName(name)] = messages[0];
  }
  return result;
}
