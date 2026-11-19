"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { ArrowLeft, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Tabs } from "@/components/ui/tabs";
import { Alert, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useDeleteZone, useZone, type Zone } from "./api";
import { ActiveBadge, FallbackBadge, KindBadge, useDeliveryRights } from "./common";
import { AreasEditor } from "./areas-editor";
import { DetailsFormCard } from "./details-form";
import { detailsToForm } from "./logic";
import { QuotePanel } from "./quote-panel";
import { RatesForm } from "./rates-form";

/** A new zone: only the details are asked; rates, areas and the quote check open on the zone's page after it is created. */
export function NewZone() {
  const t = useTranslations("delivery.editor");
  const tn = useTranslations("nav");
  const rights = useDeliveryRights();
  const noop = React.useCallback(() => undefined, []);
  return (
    <>
      <PageHeader
        title={t("newTitle")}
        description={t("newSubtitle")}
        crumbs={[{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.zones"), href: "/admin/delivery/zones" }, { label: t("newTitle") }]}
        actions={<BackButton />}
      />
      {rights.create ? <DetailsFormCard zone={null} canEdit onDirty={noop} /> : <Alert variant="info">{t("noCreate")}</Alert>}
    </>
  );
}

function BackButton() {
  const t = useTranslations("delivery.editor");
  return (
    <Button asChild variant="secondary">
      <Link href="/admin/delivery/zones">
        <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
      </Link>
    </Button>
  );
}

export function ZoneEditor({ id }: { id: string }) {
  const t = useTranslations("delivery.editor");
  const tc = useTranslations("delivery.common");
  const tn = useTranslations("nav");
  const query = useZone(id);
  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.zones"), href: "/admin/delivery/zones" },
    { label: query.data?.nameEn ?? "..." },
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
        <EmptyState title={notFound ? t("notFound") : tc("loadFailed")} action={<BackButton />} />
      </>
    );
  }
  return <Loaded zone={query.data} crumbs={crumbs} />;
}

function Loaded({ zone, crumbs }: { zone: Zone; crumbs: { label: string; href?: string }[] }) {
  const t = useTranslations("delivery.editor");
  const rights = useDeliveryRights();
  const [tab, setTab] = React.useState("details");
  const [dirty, setDirty] = React.useState({ details: false, areas: false, rates: false });
  const domestic = zone.kind === "Domestic";
  const mark = (name: keyof typeof dirty) => (value: boolean) => setDirty((d) => (d[name] === value ? d : { ...d, [name]: value }));
  const onDetails = React.useMemo(() => mark("details"), []); // eslint-disable-line react-hooks/exhaustive-deps
  const onAreas = React.useMemo(() => mark("areas"), []); // eslint-disable-line react-hooks/exhaustive-deps
  const onRates = React.useMemo(() => mark("rates"), []); // eslint-disable-line react-hooks/exhaustive-deps

  const tabs = [
    { id: "details", label: t("tabDetails") },
    ...(domestic ? [{ id: "areas", label: t("tabAreas") }] : []),
    { id: "rates", label: t("tabRates") },
    { id: "quote", label: t("tabQuote") },
  ];
  const hide = (id: string, active: string) => (id === active ? undefined : true);

  return (
    <>
      <PageHeader
        title={zone.nameEn}
        description={t("subtitle", { code: zone.code })}
        crumbs={crumbs}
        actions={
          <>
            <BackButton />
            {rights.delete ? <DeleteZone zone={zone} /> : null}
          </>
        }
      />
      <div className="space-y-4" data-testid="zone-editor">
        <div className="flex flex-wrap gap-1.5">
          <KindBadge kind={zone.kind} />
          <ActiveBadge isActive={zone.isActive} />
          {zone.isFallback ? <FallbackBadge /> : null}
        </div>
        {!zone.isActive ? (
          <Alert variant="warning" data-testid="zone-inactive-note">
            {t("inactiveNote")}
          </Alert>
        ) : null}
        <Tabs tabs={tabs} value={tab} onValueChange={setTab} label={t("tabsLabel")}>
          {(active) => (
            <>
              <div hidden={hide("details", active)}>
                <DetailsFormCard key={JSON.stringify(detailsToForm(zone))} zone={zone} canEdit={rights.edit} onDirty={onDetails} />
              </div>
              {domestic ? (
                <div hidden={hide("areas", active)}>
                  <AreasEditor key={JSON.stringify(zone.areas)} zone={zone} canEdit={rights.edit} onDirty={onAreas} />
                </div>
              ) : null}
              <div hidden={hide("rates", active)}>
                <RatesForm key={JSON.stringify([zone.baseFee, zone.freeDeliveryThreshold, zone.remoteSurcharge, zone.perExtraKgFee, zone.maxWeightGrams, zone.slabs])} zone={zone} canEdit={rights.rateEdit} onDirty={onRates} />
              </div>
              <div hidden={hide("quote", active)}>
                <QuotePanel zone={zone} unsaved={dirty.details || dirty.areas || dirty.rates} canViewCountries={rights.countryView} />
              </div>
            </>
          )}
        </Tabs>
      </div>
    </>
  );
}

/** Delete with the consequence the API enforces: not the fallback zone, not a zone a country uses; its districts then fall back to the fallback zone. */
function DeleteZone({ zone }: { zone: Zone }) {
  const t = useTranslations("delivery.delete");
  const tc = useTranslations("delivery.common");
  const router = useRouter();
  const problemMessage = useProblemMessage();
  const del = useDeleteZone();
  const [open, setOpen] = React.useState(false);
  const blocked = zone.isFallback ? "fallback" : zone.countryCodes.length > 0 ? "countries" : null;

  const confirm = async () => {
    try {
      await del.mutateAsync(zone.id);
      toast.success(t("deleted"));
      router.push("/admin/delivery/zones");
    } catch (e) {
      toast.error(problemMessage(e instanceof ApiError ? e.problem : null));
      setOpen(false);
    }
  };

  return (
    <>
      <Button variant="danger" onClick={() => setOpen(true)} data-testid="zone-delete">
        <Trash2 aria-hidden /> {t("button")}
      </Button>
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title={t("title", { name: zone.nameEn })}
        description={blocked === "fallback" ? t("blockedFallback") : blocked === "countries" ? t("blockedCountries", { codes: zone.countryCodes.join(", ") }) : t("description", { areas: zone.areas.length })}
        confirmLabel={blocked ? tc("close") : t("confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={!blocked}
        loading={del.isPending}
        onConfirm={blocked ? () => setOpen(false) : confirm}
      />
    </>
  );
}
