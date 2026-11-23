"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, Ban, CircleCheck, CircleX, MapPin, ShieldCheck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCustomer, useCustomerOrderStats, useSetCustomerActive, type CustomerAddress, type CustomerDetail } from "./api";
import { ActiveBadge, LevelBadge, MaskedNotice, useCustomerRights, usePrivacyRights } from "./common";
import { AnonymisedBanner, ConsentHistoryCard, PrivacyCard } from "./privacy-cards";
import { isAnonymisedMobile, privacySections } from "./privacy-logic";
import { CONSENT_TYPES, allowedActions, consentState, initials, isKnownConsent, type CustomerRights } from "./logic";

/** One customer for staff: profile facts, addresses, consents, sign-in summary, order history, and (with the block permission) deactivate or activate. */
export function CustomerDetailView({ id }: { id: string }) {
  const t = useTranslations("customers.detail");
  const tc = useTranslations("customers.common");
  const tn = useTranslations("nav");
  const query = useCustomer(id);
  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.customers"), href: "/admin/customers" },
    { label: query.data?.customer.fullName ?? query.data?.customer.mobile ?? "..." },
  ];

  if (query.isPending) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy="true" />
      </>
    );
  }
  if (query.isError || !query.data) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFound") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/customers">
                <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
              </Link>
            </Button>
          }
        />
      </>
    );
  }
  return <Loaded detail={query.data} crumbs={crumbs} />;
}

function Loaded({ detail, crumbs }: { detail: CustomerDetail; crumbs: { label: string; href?: string }[] }) {
  const t = useTranslations("customers.detail");
  const rights = useCustomerRights();
  const privacy = usePrivacyRights();
  const sections = privacySections({ view: rights.view, ...privacy });
  // The customer DTO has no anonymisedAt: an anonymised customer is recognised by the placeholder mobile the API writes (ANON...).
  // The date is known only when the action ran in this browser session.
  const [anonymisedAt, setAnonymisedAt] = React.useState<string | null>(null);
  const c = detail.customer;
  const anonymised = isAnonymisedMobile(c.mobile);
  const actions = anonymised ? [] : allowedActions(c.isActive, rights);
  return (
    <>
      <PageHeader
        title={c.fullName ?? t("noName")}
        description={t("subtitle")}
        crumbs={crumbs}
        actions={
          <Button asChild variant="secondary">
            <Link href="/admin/customers">
              <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
            </Link>
          </Button>
        }
      />
      <div className="space-y-4" data-testid="customer-detail">
        {detail.piiMasked ? <MaskedNotice where="detail" /> : null}
        {anonymised ? <AnonymisedBanner anonymisedAt={anonymisedAt} /> : null}
        {!c.isActive && !anonymised ? (
          <Alert variant="warning" data-testid="customer-inactive-note">
            {t("inactiveNote")}
          </Alert>
        ) : null}
        <div className="grid gap-4 lg:grid-cols-3">
          <div className="space-y-4 lg:col-span-2">
            <ProfileCard detail={detail} />
            <AddressesCard addresses={detail.addresses} masked={detail.piiMasked} />
            {sections.history ? <ConsentHistoryCard id={c.id} /> : null}
          </div>
          <div className="space-y-4">
            <AccessCard id={c.id} isActive={c.isActive} actions={actions} rights={rights} />
            <OrdersCard id={c.id} rights={rights} />
            <ConsentCard consents={detail.consents} />
            {sections.privacyCard ? <PrivacyCard id={c.id} canExport={privacy.exportData} canAnonymise={privacy.anonymise} anonymised={anonymised} onAnonymised={setAnonymisedAt} /> : null}
          </div>
        </div>
      </div>
    </>
  );
}

function Fact({ label, children, testId }: { label: string; children: React.ReactNode; testId?: string }) {
  return (
    <div className="min-w-0">
      <dt className="text-sm text-muted">{label}</dt>
      <dd className="mt-0.5 font-medium text-strong [overflow-wrap:anywhere]" data-testid={testId}>
        {children}
      </dd>
    </div>
  );
}

function ProfileCard({ detail }: { detail: CustomerDetail }) {
  const t = useTranslations("customers.detail");
  const dateTime = useDateTime();
  const c = detail.customer;
  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-3">
          <span className="flex size-12 shrink-0 items-center justify-center rounded-full bg-primary-soft font-display text-lg font-semibold text-primary-dark" aria-hidden>
            {initials(c.fullName)}
          </span>
          <div className="min-w-0">
            <CardTitle className="[overflow-wrap:anywhere]">{c.fullName ?? t("noName")}</CardTitle>
            <div className="mt-1 flex flex-wrap gap-1.5">
              <LevelBadge level={c.profileLevel} />
              <ActiveBadge isActive={c.isActive} />
            </div>
          </div>
        </div>
      </CardHeader>
      <CardContent>
        <dl className="grid gap-4 sm:grid-cols-2">
          <Fact label={t("mobile")} testId="detail-mobile">
            <span className="font-mono">{c.mobile}</span>{" "}
            {c.mobileVerified ? (
              <Badge variant="success">
                <ShieldCheck className="size-3.5" aria-hidden /> {t("verified")}
              </Badge>
            ) : (
              <Badge variant="outline">{t("notVerified")}</Badge>
            )}
          </Fact>
          <Fact label={t("email")} testId="detail-email">
            {c.email ?? <span className="font-normal text-muted">{t("noEmail")}</span>}
          </Fact>
          <Fact label={t("joined")}>{dateTime(c.createdAt)}</Fact>
          <Fact label={t("lastSignIn")}>{detail.lastSignInAt ? dateTime(detail.lastSignInAt) : <span className="font-normal text-muted">{t("neverSignedIn")}</span>}</Fact>
          <Fact label={t("signInCount")}>{toNumber(detail.signInCount)}</Fact>
          <Fact label={t("wishlist")}>{toNumber(detail.wishlistCount)}</Fact>
        </dl>
      </CardContent>
    </Card>
  );
}

function AddressesCard({ addresses, masked }: { addresses: readonly CustomerAddress[]; masked: boolean }) {
  const t = useTranslations("customers.detail");
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("addresses")}</CardTitle>
      </CardHeader>
      <CardContent>
        {addresses.length === 0 ? (
          <p className="text-muted" data-testid="no-addresses">
            {t("noAddresses")}
          </p>
        ) : (
          <>
            {masked ? <p className="mb-3 text-sm text-muted">{t("addressesMasked")}</p> : null}
            <ul className="grid gap-3 md:grid-cols-2">
              {addresses.map((a) => (
                <li key={a.id} className="rounded-card border border-line p-4" data-testid="customer-address">
                  <p className="flex flex-wrap items-center gap-2 font-semibold text-strong">
                    <MapPin className="size-4 text-muted" aria-hidden />
                    <span className="[overflow-wrap:anywhere]">{a.label ?? t("address")}</span>
                    {a.isDefault ? <Badge variant="brand">{t("default")}</Badge> : null}
                  </p>
                  <p className="mt-2 text-sm [overflow-wrap:anywhere]">{a.recipientName}</p>
                  <p className="font-mono text-sm">{a.phone}</p>
                  <p className="mt-1 text-sm text-muted [overflow-wrap:anywhere]">
                    {[a.addressLine, a.landmark, a.area, a.city, a.stateProvince, a.postalCode, a.countryCode].filter(Boolean).join(", ")}
                  </p>
                </li>
              ))}
            </ul>
          </>
        )}
      </CardContent>
    </Card>
  );
}

function ConsentCard({ consents }: { consents: CustomerDetail["consents"] }) {
  const t = useTranslations("customers.consent");
  const dateTime = useDateTime();
  const sorted = [...consents].sort((a, b) => CONSENT_TYPES.indexOf(a.type as never) - CONSENT_TYPES.indexOf(b.type as never));
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent>
        <ul className="space-y-3">
          {sorted.map((x) => {
            const state = consentState(x);
            return (
              <li key={x.type} className="flex flex-wrap items-center justify-between gap-2" data-testid={`consent-${x.type}`}>
                <span className="font-medium text-strong">{isKnownConsent(x.type) ? t(`types.${x.type}`) : x.type}</span>
                <span className="text-end">
                  <Badge variant={state === "granted" ? "success" : "outline"}>
                    {state === "granted" ? <CircleCheck className="size-3.5" aria-hidden /> : <CircleX className="size-3.5" aria-hidden />}
                    {t(state)}
                  </Badge>
                  {x.at ? <span className="mt-0.5 block text-xs text-muted">{dateTime(x.at)}</span> : null}
                </span>
              </li>
            );
          })}
        </ul>
      </CardContent>
    </Card>
  );
}

function OrdersCard({ id, rights }: { id: string; rights: CustomerRights }) {
  const t = useTranslations("customers.orders");
  const money = useMoney();
  const dateTime = useDateTime();
  const stats = useCustomerOrderStats(id, rights.viewOrderStats);
  const s = stats.data;
  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent>
        {!rights.viewOrderStats ? (
          <p className="text-sm text-muted" data-testid="orders-hidden">
            {t("noPermission")}
          </p>
        ) : stats.isPending ? (
          <Skeleton className="h-24" aria-busy="true" />
        ) : stats.isError || !s ? (
          <p className="text-sm text-muted">{t("loadFailed")}</p>
        ) : (
          <dl className="grid grid-cols-2 gap-3" data-testid="order-stats">
            <Fact label={t("total")} testId="stat-total">
              {toNumber(s.totalOrders)}
            </Fact>
            <Fact label={t("spent")} testId="stat-spent">
              {money(s.totalSpent)}
            </Fact>
            <Fact label={t("delivered")}>{toNumber(s.delivered)}</Fact>
            <Fact label={t("open")}>{toNumber(s.open)}</Fact>
            <Fact label={t("cancelled")}>{toNumber(s.cancelled)}</Fact>
            <Fact label={t("returnedOrFailed")}>{toNumber(s.returnedOrFailed)}</Fact>
            <Fact label={t("first")}>{s.firstOrderAt ? dateTime(s.firstOrderAt) : "-"}</Fact>
          </dl>
        )}
        <p className="mt-3 text-xs text-muted">{t("listNote")}</p>
      </CardContent>
    </Card>
  );
}

/** Account access: whether the customer can sign in, with deactivate or activate for people holding customers.customer.block. */
function AccessCard({ id, isActive, actions, rights }: { id: string; isActive: boolean; actions: ReturnType<typeof allowedActions>; rights: CustomerRights }) {
  const t = useTranslations("customers.access");
  const tc = useTranslations("customers.common");
  const problemMessage = useProblemMessage();
  const mutation = useSetCustomerActive(id);
  const [open, setOpen] = React.useState(false);
  const action = actions[0];

  const confirm = async () => {
    if (!action) return;
    try {
      await mutation.mutateAsync(action === "activate");
      toast.success(action === "activate" ? t("activated") : t("deactivated"));
      setOpen(false);
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      setOpen(false);
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm text-muted" data-testid="access-state">
          {isActive ? t("stateActive") : t("stateInactive")}
        </p>
        {action ? (
          <>
            <Button variant={action === "deactivate" ? "danger" : "primary"} onClick={() => setOpen(true)} data-testid={`customer-${action}`}>
              {action === "deactivate" ? <Ban aria-hidden /> : <CircleCheck aria-hidden />} {t(action)}
            </Button>
            <ConfirmDialog
              open={open}
              onOpenChange={setOpen}
              title={t(`${action}Title`)}
              description={t(`${action}Description`)}
              confirmLabel={t(action)}
              cancelLabel={tc("cancel")}
              closeLabel={tc("close")}
              destructive={action === "deactivate"}
              loading={mutation.isPending}
              onConfirm={confirm}
            />
          </>
        ) : (
          <p className="text-sm text-muted" data-testid="read-only-note">
            {rights.block ? null : t("readOnly")}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
