import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { SecurityPanel } from "@/components/auth/security-panel";
import { PageHeader } from "@/components/layout/page-header";
import { SessionsCard } from "@/components/layout/sessions-card";
import { Badge, Card, CardContent, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { requireSession } from "@/lib/server/session";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("profile");
  return { title: t("title") };
}

export default async function AdminProfilePage() {
  const { me } = await requireSession({ subject: "staff" });
  const t = await getTranslations("profile");
  const tn = await getTranslations("nav");
  const staff = me.staff!;

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: t("title") }]} />

      <div className="space-y-4">
        <Card>
          <CardHeader>
            <CardTitle>{staff.fullName}</CardTitle>
          </CardHeader>
          <CardContent>
            <dl className="grid gap-x-8 gap-y-3 sm:grid-cols-2">
              <Row label={t("email")} value={staff.email} />
              <Row label={t("userName")} value={staff.userName} />
              <Row label={t("phone")} value={staff.phone ?? "-"} />
              <Row label={t("scope")} value={staff.isGlobalScope ? t("allBranches") : t("assignedBranches")} />
            </dl>
            <h3 className="mb-2 mt-6 font-sans text-sm font-semibold text-muted">{t("roles")}</h3>
            {me.assignments.length === 0 ? (
              <p className="text-muted">{t("noRoles")}</p>
            ) : (
              <ul className="flex flex-wrap gap-2">
                {me.assignments.map((a) => (
                  <li key={a.assignmentId}>
                    <Badge variant="brand" className="px-3 py-1 text-sm">
                      {a.branchCode ?? t("everywhere")} · {a.roleName}
                    </Badge>
                  </li>
                ))}
              </ul>
            )}
          </CardContent>
        </Card>

        <SecurityPanel twoFactorEnabled={staff.twoFactorEnabled} mandatoryTwoFactor={staff.isSuperAdmin && process.env.REQUIRE_TWO_FACTOR_FOR_SUPER_ADMIN !== "false"} />
        <SessionsCard />
      </div>
    </>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="font-medium text-strong [overflow-wrap:anywhere]">{value}</dd>
    </div>
  );
}
