import type { Metadata } from "next";
import { getTranslations } from "next-intl/server";
import { PageHeader } from "@/components/layout/page-header";
import { SessionsCard } from "@/components/layout/sessions-card";
import { Badge, Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/surfaces";
import { requireSession } from "@/lib/server/session";
import { AccountSummary } from "@/modules/myaccount/account-summary";

export async function generateMetadata(): Promise<Metadata> {
  const t = await getTranslations("account");
  return { title: t("title") };
}

export default async function AccountPage() {
  const { me } = await requireSession({ subject: "customer" });
  const t = await getTranslations("account");
  const customer = me.customer!;

  return (
    <>
      <PageHeader title={t("welcome", { name: customer.fullName ?? customer.mobile })} description={t("subtitle")} />
      <div className="space-y-4">
        <Card>
          <CardHeader>
            <CardTitle>{t("profile")}</CardTitle>
            <CardDescription>{t("profileHelp")}</CardDescription>
          </CardHeader>
          <CardContent>
            <dl className="grid gap-x-8 gap-y-3 sm:grid-cols-2">
              <div>
                <dt className="text-sm text-muted">{t("mobile")}</dt>
                <dd className="font-medium text-strong" data-testid="account-mobile">
                  {customer.mobile}
                </dd>
              </div>
              <div>
                <dt className="text-sm text-muted">{t("level")}</dt>
                <dd>
                  <Badge variant="brand">{customer.profileLevel}</Badge> {customer.mobileVerified ? <Badge variant="success">{t("verified")}</Badge> : null}
                </dd>
              </div>
              <div>
                <dt className="text-sm text-muted">{t("name")}</dt>
                <dd className="font-medium text-strong">{customer.fullName ?? "-"}</dd>
              </div>
              <div>
                <dt className="text-sm text-muted">{t("email")}</dt>
                <dd className="font-medium text-strong [overflow-wrap:anywhere]">{customer.email ?? "-"}</dd>
              </div>
            </dl>
          </CardContent>
        </Card>
        <AccountSummary />
        <SessionsCard />
      </div>
    </>
  );
}
