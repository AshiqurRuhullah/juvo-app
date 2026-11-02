import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { Building2, KeyRound, ShieldCheck, UserRound } from "lucide-react";
import { PageHeader } from "@/components/layout/page-header";
import { SystemInfoCard } from "@/components/layout/system-info-card";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { requireSession } from "@/lib/server/session";
import { can } from "@/lib/auth/permissions";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("dashboard");
  return { title: t("title") };
}

/** "orders.order.view" -> "orders" */
const moduleOf = (code: string) => code.split(".")[0] ?? code;

export default async function AdminDashboardPage() {
  const { me } = await requireSession({ subject: "staff" });
  const t = await getTranslations("dashboard");
  const staff = me.staff!;

  // What the user can do in the active branch, grouped by module: the same data that builds the menu.
  const active = me.activeBranchId ?? null;
  const codes = me.hasAllPermissions ? null : Array.from(new Set([...me.globalPermissions, ...(me.isAllBranches ? Object.values(me.branchPermissions).flat() : (active ? (me.branchPermissions[active] ?? []) : []))]));
  const modules = codes ? Object.entries(codes.reduce<Record<string, number>>((acc, c) => ({ ...acc, [moduleOf(c)]: (acc[moduleOf(c)] ?? 0) + 1 }), {})).sort(([a], [b]) => a.localeCompare(b)) : [];
  const branch = me.branches.find((b) => b.id === active);
  const roles = Array.from(new Set(me.assignments.map((a) => a.roleName)));

  return (
    <>
      <PageHeader title={t("welcome", { name: staff.fullName })} description={t("subtitle")} />

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <UserRound className="size-6 text-primary" aria-hidden /> {t("you")}
            </CardTitle>
            <CardDescription>{staff.email}</CardDescription>
          </CardHeader>
          <CardContent className="space-y-3">
            <div className="flex flex-wrap gap-1.5">
              {roles.map((role) => (
                <Badge key={role} variant="brand">
                  {role}
                </Badge>
              ))}
              {staff.isSuperAdmin ? <Badge variant="sale">{t("superAdmin")}</Badge> : null}
              {staff.isGlobalScope ? <Badge variant="outline">{t("globalScope")}</Badge> : null}
            </div>
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
              <dt className="text-muted">{t("twoFactor")}</dt>
              <dd className="text-end text-strong">{staff.twoFactorEnabled ? t("on") : t("off")}</dd>
              <dt className="text-muted">{t("lastSignIn")}</dt>
              <dd className="text-end text-strong">{staff.lastLoginAt ? new Date(staff.lastLoginAt).toLocaleString() : "-"}</dd>
            </dl>
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <Building2 className="size-6 text-primary" aria-hidden /> {t("workingIn")}
            </CardTitle>
            <CardDescription>{t("workingInHelp")}</CardDescription>
          </CardHeader>
          <CardContent className="space-y-2">
            <p className="font-display text-xl font-semibold text-strong">{me.isAllBranches ? t("allBranches") : (branch?.nameEn ?? t("noBranch"))}</p>
            {branch ? <p className="text-sm text-muted">{branch.code}</p> : null}
            {!branch && !me.isAllBranches ? <p className="text-sm text-muted">{t("chooseBranchHint")}</p> : null}
            <p className="text-sm text-muted">{t("branchCount", { count: me.branches.length })}</p>
          </CardContent>
        </Card>

        <SystemInfoCard />

        <Card className="md:col-span-2 xl:col-span-3">
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <KeyRound className="size-6 text-primary" aria-hidden /> {t("access")}
            </CardTitle>
            <CardDescription>{t("accessHelp")}</CardDescription>
          </CardHeader>
          <CardContent>
            {me.hasAllPermissions ? (
              <p className="flex items-center gap-2 text-strong">
                <ShieldCheck className="size-5 text-success" aria-hidden /> {t("allPermissions", { version: me.permissionCatalogVersion })}
              </p>
            ) : modules.length === 0 ? (
              <p className="text-muted">{t("noPermissions")}</p>
            ) : (
              <ul className="flex flex-wrap gap-2">
                {modules.map(([module, count]) => (
                  <li key={module}>
                    <Badge variant="neutral" className="px-3 py-1 text-sm">
                      {module} · {count}
                    </Badge>
                  </li>
                ))}
              </ul>
            )}
            <p className="mt-4 text-sm text-muted">{can(me, "ums.user.view", "anywhere") ? t("canManageUsers") : t("cannotManageUsers")}</p>
          </CardContent>
        </Card>
      </div>
    </>
  );
}
