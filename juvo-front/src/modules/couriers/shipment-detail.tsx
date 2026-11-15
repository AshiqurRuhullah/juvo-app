"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { ArrowLeft, CircleX, FastForward, FlaskConical, Printer, RefreshCw, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { LiveBadge } from "@/components/ui/live-badge";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { usePermissions } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useLiveRefresh, type LiveEventName } from "@/lib/realtime/use-live";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { COURIER_KEYS, useShipment, type Shipment, type ShipmentEvent } from "./api";
import { defaultNext } from "./admin-logic";
import { JourneyBar, ShipmentStatusBadge, TrackingLink } from "./common";
import type { DialogKind } from "./shipment-dialogs";
import { useCourierPermissions } from "./permissions";
import { formatWeight } from "./status";

const LIVE_EVENTS: LiveEventName[] = ["ShipmentEventAdded"];
const ShipmentDialogs = dynamic(() => import("./shipment-dialogs"), { ssr: false });

/** One parcel: where it is on its journey, the facts, the immutable event log and the actions this person may take. */
export function ShipmentDetailView({ id }: { id: string }) {
  const t = useTranslations("shipments.detail");
  const tc = useTranslations("shipments.common");
  const tn = useTranslations("nav");
  const query = useShipment(id);
  const { activeBranchId, isAllBranches } = usePermissions();
  const data = query.data;

  // Follow the parcel's order and the active branch. Events can arrive twice, so the answer is to reload the parcel, not to append.
  const liveKeys = React.useMemo(() => [[...COURIER_KEYS.shipments, "one", id]], [id]);
  const orderId = data?.orderId;
  const orderIds = React.useMemo(() => (orderId ? [orderId] : []), [orderId]);
  const live = useLiveRefresh({ mode: "staff", branch: isAllBranches ? "all" : activeBranchId, orderIds, queryKeys: liveKeys, events: LIVE_EVENTS, enabled: !!orderId });

  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.shipments"), href: "/admin/shipments" },
    { label: data?.consignmentId ?? "..." },
  ];

  if (query.isPending) {
    return (
      <>
        <PageHeader title="..." crumbs={crumbs} />
        <Skeleton className="h-64" aria-busy="true" />
      </>
    );
  }
  if (query.isError || !data) {
    const notFound = query.error instanceof ApiError && query.error.problem.status === 404;
    return (
      <>
        <PageHeader title={t("notFound")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFound") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/shipments">
                <ArrowLeft className="rtl:rotate-180" aria-hidden /> {t("back")}
              </Link>
            </Button>
          }
        />
      </>
    );
  }

  return <Loaded shipment={data} live={live} crumbs={crumbs} />;
}

function Loaded({ shipment: s, live, crumbs }: { shipment: Shipment; live: React.ComponentProps<typeof LiveBadge>["status"]; crumbs: { label: string; href?: string }[] }) {
  const t = useTranslations("shipments.detail");
  const tk = useTranslations("shipments.kind");
  const dateTime = useDateTime();
  const money = useMoney();
  const perms = useCourierPermissions(s.branchId);
  const [dialog, setDialog] = React.useState<DialogKind | null>(null);
  const closeDialog = React.useCallback(() => setDialog(null), []);

  const cod = toNumber(s.codAmount);
  const collected = s.codCollectedAmount === null ? null : toNumber(s.codCollectedAmount);
  const dims = [s.lengthCm, s.widthCm, s.heightCm].every((d) => d !== null) ? `${toNumber(s.lengthCm)} × ${toNumber(s.widthCm)} × ${toNumber(s.heightCm)} cm` : null;
  const failed = s.status === "DeliveryFailed";

  const facts: { label: string; value: React.ReactNode }[] = [
    { label: t("courier"), value: s.courierName },
    { label: t("consignment"), value: <span className="font-mono">{s.consignmentId}</span> },
    { label: t("weight"), value: formatWeight(toNumber(s.weightGrams)) },
    ...(dims ? [{ label: t("dimensions"), value: dims }] : []),
    { label: t("courierCost"), value: money(toNumber(s.cost)) },
    { label: t("attempts"), value: toNumber(s.attempts) },
    { label: t("booked"), value: dateTime(s.bookedAt) },
    ...(s.pickedUpAt ? [{ label: t("pickedUp"), value: dateTime(s.pickedUpAt) }] : []),
    ...(s.deliveredAt ? [{ label: t("delivered"), value: dateTime(s.deliveredAt) }] : []),
    ...(s.returnedAt ? [{ label: t("returned"), value: dateTime(s.returnedAt) }] : []),
    ...(s.cancelledAt ? [{ label: t("cancelled"), value: dateTime(s.cancelledAt) }] : []),
  ];

  return (
    <>
      <PageHeader
        title={s.consignmentId}
        description={t("subtitle", { order: s.orderNumber, courier: s.courierName })}
        crumbs={crumbs}
        actions={
          <>
            <ShipmentStatusBadge status={s.status} />
            <Badge variant="outline">{tk(s.kind)}</Badge>
            <LiveBadge status={live} />
          </>
        }
      />

      <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,24rem)]">
        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>{t("journey")}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <JourneyBar status={s.status} />
              {failed || s.failureReason ? (
                <Alert variant="danger" title={t("failedTitle")} data-testid="failure-reason">
                  {s.failureReason ?? t("failedNoReason")}
                </Alert>
              ) : null}
              {s.status === "Cancelled" ? (
                <Alert variant="info" title={t("cancelledTitle")} data-testid="cancel-reason">
                  {s.cancelReason ?? "-"}
                </Alert>
              ) : null}
              <Actions shipment={s} perms={perms} onOpen={setDialog} />
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("timeline")}</CardTitle>
              <p className="text-sm text-muted">{t("timelineHint")}</p>
            </CardHeader>
            <CardContent>
              <Timeline events={s.events} />
            </CardContent>
          </Card>
        </div>

        <div className="space-y-6">
          <Card>
            <CardHeader>
              <CardTitle>{t("facts")}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              <dl className="space-y-2" data-testid="shipment-facts">
                {facts.map((f) => (
                  <div key={f.label} className="flex items-baseline justify-between gap-3">
                    <dt className="text-muted">{f.label}</dt>
                    <dd className="text-end font-medium text-strong [overflow-wrap:anywhere]">{f.value}</dd>
                  </div>
                ))}
              </dl>
              <TrackingLink url={s.trackingUrl} />
              {s.note ? (
                <p className="rounded-control bg-sunken p-3 text-sm text-strong [overflow-wrap:anywhere]">
                  <span className="font-semibold">{t("note")}: </span>
                  {s.note}
                </p>
              ) : null}
              <div className="flex flex-wrap gap-2 border-t border-line pt-3">
                <Button asChild variant="secondary">
                  <Link href={`/admin/orders/${s.orderId}`} data-testid="order-link">
                    {t("openOrder", { number: s.orderNumber })}
                  </Link>
                </Button>
                {s.kind === "Return" && s.returnRequestId ? (
                  <Button asChild variant="secondary">
                    <Link href={`/admin/returns/${s.returnRequestId}`} data-testid="return-link">
                      {t("openReturn")}
                    </Link>
                  </Button>
                ) : null}
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("cash")}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-2" data-testid="shipment-cod">
              {cod > 0 ? (
                <>
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted">{t("toCollect")}</span>
                    <span className="text-lg font-bold tabular-nums text-strong" data-testid="cod-amount">{money(cod)}</span>
                  </div>
                  <div className="flex items-baseline justify-between gap-3">
                    <span className="text-muted">{t("collectedAmount")}</span>
                    <span className="font-medium tabular-nums text-strong">{collected === null ? t("notCollected") : money(collected)}</span>
                  </div>
                  {s.codCollectedAt ? <p className="text-sm text-muted">{t("collectedAt", { at: dateTime(s.codCollectedAt) })}</p> : null}
                  {s.codSettlementId ? <p className="text-sm text-muted">{t("inSettlement")}</p> : null}
                </>
              ) : (
                <p className="text-muted">{t("noCash")}</p>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>{t("addresses")}</CardTitle>
            </CardHeader>
            <CardContent className="space-y-4">
              <AddressBlock title={t("pickup")} name={s.pickupName} phone={s.pickupPhone} address={s.pickupAddress} />
              <AddressBlock title={t("delivery")} name={s.deliveryName} phone={s.deliveryPhone} address={s.deliveryAddress} district={s.deliveryDistrict} />
            </CardContent>
          </Card>
        </div>
      </div>

      {dialog ? <ShipmentDialogs kind={dialog} shipment={s} onClose={closeDialog} /> : null}
    </>
  );
}

function AddressBlock({ title, name, phone, address, district }: { title: string; name: string; phone?: string | null; address: string; district?: string | null }) {
  return (
    <section aria-label={title}>
      <h3 className="mb-1 text-sm font-semibold text-muted">{title}</h3>
      <p className="font-medium text-strong [overflow-wrap:anywhere]">{name}</p>
      {phone ? <p className="text-strong">{phone}</p> : null}
      <p className="text-strong [overflow-wrap:anywhere]">{address}</p>
      {district ? <p className="text-sm text-muted">{district}</p> : null}
    </section>
  );
}

/** Buttons are driven by the API's own flags (`canCancel`, `canSimulate`, `nextStatuses`) and by this person's permissions in the parcel's branch. */
function Actions({ shipment: s, perms, onOpen }: { shipment: Shipment; perms: ReturnType<typeof useCourierPermissions>; onOpen: (kind: DialogKind) => void }) {
  const t = useTranslations("shipments.actions");
  const ts = useTranslations("shipments.status");
  const next = defaultNext(s.nextStatuses);
  const showCancel = s.canCancel && perms.cancel;
  const showRebook = perms.book && (s.canCancel || s.status === "Cancelled");
  const showAdvance = s.canSimulate && perms.simulate && s.nextStatuses.some((x) => x !== "Cancelled");
  const showLabel = perms.printLabel && s.status !== "Cancelled";
  if (!showCancel && !showRebook && !showAdvance && !showLabel) return null;

  return (
    <div className="space-y-3 border-t border-line pt-4" data-testid="shipment-actions">
      {showAdvance ? (
        <div className="space-y-2 rounded-control border border-info/30 bg-info-soft p-3" data-testid="simulator-box">
          <p className="flex items-center gap-2 text-sm font-semibold text-info-ink">
            <FlaskConical className="size-4" aria-hidden /> {t("simulatorTitle")}
          </p>
          <p className="text-sm text-strong">{t("simulatorHint")}</p>
          <div className="flex flex-wrap gap-2">
            {next ? (
              <Button onClick={() => onOpen("advance")} data-testid="action-advance">
                <FastForward aria-hidden /> {t("advance", { status: ts(next) })}
              </Button>
            ) : null}
          </div>
        </div>
      ) : null}
      <div className="flex flex-wrap gap-2">
        {showLabel ? (
          <Button asChild variant="secondary">
            <Link href={`/admin/shipments/${s.id}/label`} data-testid="action-label">
              <Printer aria-hidden /> {t("printLabel")}
            </Link>
          </Button>
        ) : null}
        {showRebook ? (
          <Button variant="secondary" onClick={() => onOpen("rebook")} data-testid="action-rebook">
            <RefreshCw aria-hidden /> {t("rebook")}
          </Button>
        ) : null}
        {showCancel ? (
          <Button variant="secondary" onClick={() => onOpen("cancel")} data-testid="action-cancel">
            <XCircle aria-hidden /> {t("cancel")}
          </Button>
        ) : null}
      </div>
    </div>
  );
}

/** The immutable event log, newest first. Failed deliveries carry an icon as well as colour. */
function Timeline({ events }: { events: ShipmentEvent[] }) {
  const t = useTranslations("shipments.detail");
  const ts = useTranslations("shipments.status");
  const tsrc = useTranslations("shipments.source");
  const dateTime = useDateTime();
  const ordered = React.useMemo(() => [...events].reverse(), [events]);
  if (ordered.length === 0) return <p className="text-muted">{t("noEvents")}</p>;
  return (
    <ol className="space-y-0" aria-label={t("timeline")} data-testid="timeline">
      {ordered.map((e) => (
        <li key={e.id} className="relative border-s-2 border-line pb-5 ps-4 last:pb-0" data-testid="timeline-event" data-status={e.status}>
          <span className="absolute -start-[7px] top-1.5 size-3 rounded-full border-2 border-card bg-primary" aria-hidden />
          <div className="flex flex-wrap items-center gap-2">
            <span className="inline-flex items-center gap-1 font-semibold text-strong">
              {e.status === "DeliveryFailed" ? <CircleX className="size-4 text-danger" aria-hidden /> : null}
              {e.isStatusChange ? ts(e.status) : t("scan")}
            </span>
            <Badge variant="outline">{tsrc(e.source)}</Badge>
            <time className="text-sm text-muted" dateTime={e.occurredAt}>
              {dateTime(e.occurredAt, "long")}
            </time>
          </div>
          {e.description ? <p className="mt-1 text-strong [overflow-wrap:anywhere]">{e.description}</p> : null}
          <p className="mt-0.5 flex flex-wrap gap-x-3 text-sm text-muted [overflow-wrap:anywhere]">
            {e.location ? <span>{t("location", { place: e.location })}</span> : null}
            {e.actorName ? <span>{t("actor", { name: e.actorName })}</span> : null}
            <span className="font-mono">{e.rawStatus}</span>
          </p>
        </li>
      ))}
    </ol>
  );
}
