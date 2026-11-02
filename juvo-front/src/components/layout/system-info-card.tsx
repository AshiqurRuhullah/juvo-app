"use client";

import { useQuery } from "@tanstack/react-query";
import { useTranslations } from "next-intl";
import { Server } from "lucide-react";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle, Skeleton } from "@/components/ui/surfaces";
import { api, unwrap } from "@/lib/api/client";

/** Live data from the API through the typed client: name, version and environment. Shows the connection works end to end. */
export function SystemInfoCard() {
  const t = useTranslations("dashboard");
  const { data, isLoading, isError } = useQuery({
    queryKey: ["system", "info"],
    queryFn: async () => unwrap(await api.GET("/api/v1/system/info")),
    staleTime: 60_000,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <Server className="size-6 text-primary" aria-hidden /> {t("system")}
        </CardTitle>
        <CardDescription>{t("systemHelp")}</CardDescription>
      </CardHeader>
      <CardContent>
        {isLoading ? (
          <div className="space-y-2">
            <Skeleton className="h-5 w-40" />
            <Skeleton className="h-5 w-28" />
          </div>
        ) : isError || !data ? (
          <Badge variant="danger">{t("systemDown")}</Badge>
        ) : (
          <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm" data-testid="system-info">
            <dt className="text-muted">{t("api")}</dt>
            <dd className="text-end text-strong">{data.name}</dd>
            <dt className="text-muted">{t("version")}</dt>
            <dd className="text-end text-strong [overflow-wrap:anywhere]">{data.version.split("+")[0]}</dd>
            <dt className="text-muted">{t("environment")}</dt>
            <dd className="text-end">
              <Badge variant={data.environment === "Production" ? "success" : "info"}>{data.environment}</Badge>
            </dd>
          </dl>
        )}
      </CardContent>
    </Card>
  );
}
