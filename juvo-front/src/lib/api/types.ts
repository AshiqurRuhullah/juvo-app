import type { components } from "./schema";

/** Shorthand for the generated API types (src/lib/api/schema.d.ts, produced by `npm run gen:api`). */
export type Schemas = components["schemas"];

export type Me = Schemas["MeResponse"];
export type StaffProfile = Schemas["StaffProfileDto"];
export type CustomerProfile = Schemas["CustomerProfileDto"];
export type BranchSummary = Schemas["BranchSummaryDto"];
export type RoleAssignment = Schemas["RoleAssignmentDto"];
export type SessionDto = Schemas["SessionDto"];
export type ProblemDetails = Schemas["ProblemDetails"];

/** What the API sends back for every error (RFC 9457 plus our stable `code`). */
export type ApiProblem = {
  status?: number;
  title?: string;
  detail?: string;
  code?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
};
