"use client";

import * as React from "react";
import { useFormatter, useTranslations } from "next-intl";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { MonitorSmartphone } from "lucide-react";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { Button } from "@/components/ui/button";
import { DataTable } from "@/components/ui/data-table";
import { toast } from "@/components/ui/toaster";
import { api, unwrap } from "@/lib/api/client";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import type { SessionDto } from "@/lib/api/types";
import type { ColumnDef } from "@tanstack/react-table";

/** Devices signed in as me, with a button to sign one out. Works for staff and customers (/auth/sessions). */
export function SessionsCard() {
  const t = useTranslations("sessions");
  const format = useFormatter();
  const message = useProblemMessage();
  const queryClient = useQueryClient();

  const { data, isLoading } = useQuery({
    queryKey: ["auth", "sessions"],
    queryFn: async () => unwrap(await api.GET("/api/v1/auth/sessions")),
  });

  const revoke = useMutation({
    mutationFn: async (sessionId: string) => unwrap(await api.DELETE("/api/v1/auth/sessions/{sessionId}", { params: { path: { sessionId } } })),
    onSuccess: () => {
      toast.success(t("revoked"));
      void queryClient.invalidateQueries({ queryKey: ["auth", "sessions"] });
    },
    onError: (error) => toast.error(message(error instanceof ApiError ? error.problem : null)),
  });

  const columns = React.useMemo<ColumnDef<SessionDto, unknown>[]>(
    () => [
      {
        id: "device",
        header: t("device"),
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="flex flex-wrap items-center gap-2">
            <span className="[overflow-wrap:anywhere]">{summarizeAgent(row.original.deviceName ?? row.original.userAgent)}</span>
            {row.original.isCurrent ? <Badge variant="brand">{t("thisDevice")}</Badge> : null}
          </span>
        ),
      },
      { id: "ip", header: t("ip"), cell: ({ row }) => row.original.ipAddress ?? "-" },
      { id: "started", header: t("started"), cell: ({ row }) => format.dateTime(new Date(row.original.startedAt), { dateStyle: "medium", timeStyle: "short" }) },
      { id: "expires", header: t("expires"), meta: { hideOnMobile: true }, cell: ({ row }) => format.dateTime(new Date(row.original.expiresAt), { dateStyle: "medium" }) },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end" },
        cell: ({ row }) =>
          row.original.isCurrent ? null : (
            <Button variant="secondary" onClick={() => revoke.mutate(row.original.sessionId)} loading={revoke.isPending && revoke.variables === row.original.sessionId}>
              {t("signOutDevice")}
            </Button>
          ),
      },
    ],
    [t, format, revoke],
  );

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <MonitorSmartphone className="size-6 text-primary" aria-hidden /> {t("title")}
        </CardTitle>
        <CardDescription>{t("description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <DataTable
          caption={t("title")}
          columns={columns}
          data={data ?? []}
          loading={isLoading}
          getRowId={(s) => s.sessionId}
          labels={{ empty: t("empty"), previous: "", next: "", pageOf: () => "", total: () => "", sortBy: () => "" }}
        />
      </CardContent>
    </Card>
  );
}

/** "Chrome on Windows" from a user-agent string, short enough for a table cell. */
export function summarizeAgent(value: string | null | undefined): string {
  if (!value) return "-";
  const browser = /Edg\//.test(value) ? "Edge" : /OPR\//.test(value) ? "Opera" : /Chrome\//.test(value) ? "Chrome" : /Firefox\//.test(value) ? "Firefox" : /Safari\//.test(value) ? "Safari" : null;
  const os = /Windows/.test(value) ? "Windows" : /Android/.test(value) ? "Android" : /iPhone|iPad/.test(value) ? "iOS" : /Mac OS/.test(value) ? "macOS" : /Linux/.test(value) ? "Linux" : null;
  if (browser && os) return `${browser} · ${os}`;
  return browser ?? os ?? value.slice(0, 40);
}
