"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { usePermissions } from "@/components/providers/session-provider";
import { useRevokeUserSession, useReportError, useUserSessions, useUserSignIns, type Session, type SignInHistory } from "./api";
import { useDateTime, useTableLabels } from "./shared";

/** Short device text from a user-agent string ("Chrome on Windows"). The raw string is never trusted for markup; it is only text. */
export function summarizeAgent(agent: string | null | undefined): string {
  if (!agent) return "-";
  const browser = /Edg\//.test(agent) ? "Edge" : /OPR\//.test(agent) ? "Opera" : /Chrome\//.test(agent) ? "Chrome" : /Firefox\//.test(agent) ? "Firefox" : /Safari\//.test(agent) ? "Safari" : null;
  const os = /Windows/.test(agent) ? "Windows" : /Android/.test(agent) ? "Android" : /iPhone|iPad/.test(agent) ? "iOS" : /Mac OS X/.test(agent) ? "macOS" : /Linux/.test(agent) ? "Linux" : null;
  return browser && os ? `${browser} · ${os}` : (browser ?? os ?? agent.slice(0, 60));
}

export function UserSessions({ userId }: { userId: string }) {
  const t = useTranslations("ums.sessions");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const report = useReportError();
  const { can } = usePermissions();
  const query = useUserSessions(userId);
  const revoke = useRevokeUserSession(userId);
  const canRevoke = can("ums.session.revoke", "anywhere");

  const columns = React.useMemo<ColumnDef<Session, unknown>[]>(
    () => [
      { id: "device", header: t("device"), meta: { primary: true }, cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{summarizeAgent(row.original.deviceName ?? row.original.userAgent)}</span> },
      { id: "ip", header: t("ip"), cell: ({ row }) => row.original.ipAddress ?? "-" },
      { id: "started", header: t("started"), cell: ({ row }) => dateTime(row.original.startedAt) },
      { id: "expires", header: t("expires"), meta: { hideOnMobile: true }, cell: ({ row }) => dateTime(row.original.expiresAt) },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("actions") },
        cell: ({ row }) =>
          canRevoke ? (
            <Button
              variant="secondary"
              loading={revoke.isPending && revoke.variables === row.original.sessionId}
              onClick={async () => {
                try {
                  await revoke.mutateAsync(row.original.sessionId);
                  toast.success(t("revoked"));
                } catch (error) {
                  report(error);
                }
              }}
            >
              {t("signOutDevice")}
            </Button>
          ) : null,
      },
    ],
    [t, dateTime, canRevoke, revoke, report],
  );

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <DataTable caption={t("title")} columns={columns} data={query.data ?? []} loading={query.isLoading} getRowId={(s) => s.sessionId} labels={{ ...labels, empty: t("empty"), emptyDescription: undefined }} />
      </CardContent>
    </Card>
  );
}

/** Columns shared by the user's own history and the company-wide sign-in page. */
export function useSignInColumns(options: { showIdentifier: boolean }): ColumnDef<SignInHistory, unknown>[] {
  const t = useTranslations("ums.signIns");
  const dateTime = useDateTime();
  return React.useMemo<ColumnDef<SignInHistory, unknown>[]>(
    () => [
      { id: "occurredAt", header: t("when"), meta: { primary: true }, cell: ({ row }) => dateTime(row.original.occurredAt, "long") },
      ...(options.showIdentifier ? [{ id: "identifier", header: t("who"), cell: ({ row }) => <span className="[overflow-wrap:anywhere]">{row.original.identifier}</span> } satisfies ColumnDef<SignInHistory, unknown>] : []),
      {
        id: "result",
        header: t("result"),
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.succeeded ? (
            <Badge variant="success">{t("succeeded")}</Badge>
          ) : (
            <span className="flex flex-wrap items-center gap-1.5">
              <Badge variant="danger">{t("failed")}</Badge>
              {row.original.failureReason ? <span className="text-sm text-muted">{row.original.failureReason}</span> : null}
            </span>
          ),
      },
      { id: "ip", header: t("ip"), cell: ({ row }) => row.original.ipAddress ?? "-" },
      { id: "device", header: t("device"), meta: { hideOnMobile: true }, cell: ({ row }) => summarizeAgent(row.original.userAgent) },
    ],
    [t, dateTime, options.showIdentifier],
  );
}

export function UserSignIns({ userId }: { userId: string }) {
  const t = useTranslations("ums.signIns");
  const labels = useTableLabels();
  const [page, setPage] = React.useState(1);
  const query = useUserSignIns(userId, page);
  const columns = useSignInColumns({ showIdentifier: false });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <DataTable
          caption={t("title")}
          columns={columns}
          data={query.data?.items ?? []}
          loading={query.isLoading}
          getRowId={(s) => s.id}
          labels={{ ...labels, empty: t("empty"), emptyDescription: undefined }}
          page={page}
          pageSize={10}
          total={Number(query.data?.total ?? 0)}
          onPageChange={setPage}
        />
      </CardContent>
    </Card>
  );
}
