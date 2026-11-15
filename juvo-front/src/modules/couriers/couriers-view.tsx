"use client";

import * as React from "react";
import dynamic from "next/dynamic";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Archive, CircleCheck, CircleDashed, FlaskConical, Pencil, Plus, ShieldCheck, Truck } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Switch } from "@/components/ui/form";
import { Tabs } from "@/components/ui/tabs";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useArchiveCourier, useCourierProviders, useCouriers, useSetCourierEnabled, type Courier, type CourierProvider } from "./api";
import { useCourierPermissions } from "./permissions";
import { formatWeight } from "./status";

// The form is only needed when someone opens it, so it is a separate chunk.
const CourierFormDialog = dynamic(() => import("./courier-form"), { ssr: false });

type Notice = { id: string; message: string };

/** `/admin/couriers`: the couriers (rates, rules, switches) and the connections (which adapters are set up). */
export function CouriersView() {
  const t = useTranslations("couriers");
  const [tab, setTab] = React.useState<"couriers" | "connections">("couriers");
  return (
    <Tabs
      label={t("tabsLabel")}
      value={tab}
      onValueChange={(id) => setTab(id as typeof tab)}
      tabs={[
        { id: "couriers", label: t("tabs.couriers") },
        { id: "connections", label: t("tabs.connections") },
      ]}
    >
      {(active) => (active === "couriers" ? <CourierList /> : <Connections />)}
    </Tabs>
  );
}

function CourierList() {
  const t = useTranslations("couriers.list");
  const tc = useTranslations("couriers.common");
  const perms = useCourierPermissions();
  const query = useCouriers();
  const [editing, setEditing] = React.useState<Courier | null>(null);
  const [formOpen, setFormOpen] = React.useState(false);
  const [notice, setNotice] = React.useState<Notice | null>(null);
  const [archiving, setArchiving] = React.useState<Courier | null>(null);
  const items = query.data ?? [];

  const openForm = React.useCallback((courier: Courier | null) => {
    setEditing(courier);
    setFormOpen(true);
  }, []);
  const onNotice = React.useCallback((n: Notice | null) => setNotice(n), []);
  const onArchive = React.useCallback((c: Courier) => setArchiving(c), []);

  return (
    <div className="mt-4 space-y-4">
      {perms.courierEdit ? (
        <div className="flex justify-end">
          <Button onClick={() => openForm(null)} data-testid="courier-add">
            <Plus aria-hidden /> {t("add")}
          </Button>
        </div>
      ) : (
        <Alert variant="info">{t("readOnly")}</Alert>
      )}

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}

      {query.isPending ? (
        <div className="grid gap-4 lg:grid-cols-2" aria-busy="true">
          <Skeleton className="h-64" />
          <Skeleton className="h-64" />
        </div>
      ) : items.length === 0 && !query.isError ? (
        <EmptyState icon={<Truck />} title={t("empty")} description={t("emptyHint")} />
      ) : (
        <ul className="grid gap-4 lg:grid-cols-2" aria-label={t("title")}>
          {items.map((courier) => (
            <li key={courier.id}>
              <CourierCard courier={courier} canEdit={perms.courierEdit} notice={notice?.id === courier.id ? notice.message : null} onNotice={onNotice} onEdit={openForm} onArchive={onArchive} />
            </li>
          ))}
        </ul>
      )}

      {formOpen ? <CourierFormDialog open={formOpen} onOpenChange={setFormOpen} courier={editing} /> : null}
      <ArchiveDialog courier={archiving} onClose={() => setArchiving(null)} />
    </div>
  );
}

const CourierCard = React.memo(function CourierCard({
  courier,
  canEdit,
  notice,
  onNotice,
  onEdit,
  onArchive,
}: {
  courier: Courier;
  canEdit: boolean;
  notice: string | null;
  onNotice: (n: Notice | null) => void;
  onEdit: (c: Courier) => void;
  onArchive: (c: Courier) => void;
}) {
  const t = useTranslations("couriers.card");
  const tc = useTranslations("couriers.common");
  const money = useMoney();
  const problemMessage = useProblemMessage();
  const setEnabled = useSetCourierEnabled();
  const open = toNumber(courier.openShipments);
  const override = courier.chargeOverride === null ? null : toNumber(courier.chargeOverride);

  const toggle = async (isEnabled: boolean) => {
    onNotice(null);
    try {
      await setEnabled.mutateAsync({ id: courier.id, isEnabled });
      toast.success(isEnabled ? t("switchedOn", { name: courier.name }) : t("switchedOff", { name: courier.name }));
    } catch (e) {
      // A slot without account keys answers 409 COURIER.NOT_CONFIGURED: the API's own explanation is shown, never a raw error.
      onNotice({ id: courier.id, message: problemMessage(e instanceof ApiError ? e.problem : null) });
    }
  };

  const facts: { label: string; value: React.ReactNode }[] = [
    { label: t("priority"), value: toNumber(courier.priority) },
    { label: t("cod"), value: courier.codSupported ? tc("yes") : tc("no") },
    { label: t("international"), value: courier.supportsInternational ? tc("yes") : tc("no") },
    {
      label: t("weight"),
      value:
        courier.minWeightGrams === null && courier.maxWeightGrams === null
          ? t("anyWeight")
          : `${courier.minWeightGrams === null ? "0 g" : formatWeight(toNumber(courier.minWeightGrams))} - ${courier.maxWeightGrams === null ? t("noLimit") : formatWeight(toNumber(courier.maxWeightGrams))}`,
    },
    { label: t("zones"), value: courier.supportedZoneCodes.length === 0 ? t("allZones") : courier.supportedZoneCodes.join(", ") },
    { label: t("rate"), value: t("rateValue", { base: money(toNumber(courier.baseRate)), grams: formatWeight(toNumber(courier.baseRateUpToGrams)), extra: money(toNumber(courier.perExtraKgRate)) }) },
    { label: t("override"), value: override === null ? t("noOverride") : money(override) },
    { label: t("openParcels"), value: open > 0 ? <Link href={`/admin/shipments`} className="inline-flex min-h-11 items-center font-semibold text-primary hover:underline">{open}</Link> : 0 },
  ];

  return (
    <Card data-testid="courier-card" data-code={courier.code} data-enabled={courier.isEnabled}>
      <CardHeader className="gap-3">
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0">
            <CardTitle className="[overflow-wrap:anywhere]">{courier.name}</CardTitle>
            <p className="font-mono text-sm text-muted">{courier.code}</p>
          </div>
          <label className="flex min-h-11 shrink-0 cursor-pointer items-center gap-2 text-sm font-medium text-strong">
            <Switch checked={courier.isEnabled} disabled={!canEdit || setEnabled.isPending} onCheckedChange={(v) => void toggle(v)} aria-label={t("enabledSwitch", { name: courier.name })} />
            <span aria-hidden>{courier.isEnabled ? t("on") : t("off")}</span>
          </label>
        </div>
        <div className="flex flex-wrap gap-2">
          <Badge variant="outline">{courier.providerName}</Badge>
          {courier.isSimulator ? (
            <Badge variant="info" data-testid="simulator-badge">
              <FlaskConical className="size-3.5" aria-hidden /> {t("simulator")}
            </Badge>
          ) : null}
          {courier.providerConfigured ? (
            <Badge variant="success">
              <CircleCheck className="size-3.5" aria-hidden /> {t("connected")}
            </Badge>
          ) : (
            <Badge variant="warning" data-testid="not-connected">
              <CircleDashed className="size-3.5" aria-hidden /> {t("notConnected")}
            </Badge>
          )}
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        {notice ? (
          <Alert variant="warning" data-testid="courier-notice" role="alert">
            {notice}
          </Alert>
        ) : null}
        {!courier.providerConfigured && !notice ? <p className="text-sm text-muted">{t("notConnectedHint")}</p> : null}
        <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2">
          {facts.map((f) => (
            <div key={f.label} className="flex items-baseline justify-between gap-3 border-b border-line py-1 sm:block sm:border-0">
              <dt className="text-sm text-muted">{f.label}</dt>
              <dd className="text-end font-medium text-strong [overflow-wrap:anywhere] sm:text-start">{f.value}</dd>
            </div>
          ))}
        </dl>
        {canEdit ? (
          <div className="flex flex-wrap gap-2 pt-1">
            <Button variant="secondary" onClick={() => onEdit(courier)}>
              <Pencil aria-hidden /> {t("edit")}
              <span className="sr-only"> {courier.name}</span>
            </Button>
            <Button variant="ghost" onClick={() => onArchive(courier)}>
              <Archive aria-hidden /> {t("archive")}
              <span className="sr-only"> {courier.name}</span>
            </Button>
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
});

function ArchiveDialog({ courier, onClose }: { courier: Courier | null; onClose: () => void }) {
  const t = useTranslations("couriers.archive");
  const tc = useTranslations("couriers.common");
  const problemMessage = useProblemMessage();
  const archive = useArchiveCourier();
  const [failure, setFailure] = React.useState<string | null>(null);
  return (
    <ConfirmDialog
      open={courier !== null}
      onOpenChange={(open) => {
        if (!open) {
          setFailure(null);
          onClose();
        }
      }}
      title={t("title", { name: courier?.name ?? "" })}
      description={t("description")}
      confirmLabel={t("confirm")}
      cancelLabel={tc("cancel")}
      closeLabel={tc("close")}
      destructive
      loading={archive.isPending}
      onConfirm={async () => {
        if (!courier) return;
        try {
          await archive.mutateAsync(courier.id);
          toast.success(t("done", { name: courier.name }));
          setFailure(null);
          onClose();
        } catch (e) {
          setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
        }
      }}
    >
      {failure ? <Alert variant="danger" data-testid="archive-error">{failure}</Alert> : null}
    </ConfirmDialog>
  );
}

/** Which adapters exist and which are set up. Honest about the slots: real courier accounts are connected later, with the owner's keys. */
function Connections() {
  const t = useTranslations("couriers.connections");
  const tc = useTranslations("couriers.common");
  const query = useCourierProviders();
  const couriers = useCouriers();
  const used = React.useMemo(() => {
    const map = new Map<string, number>();
    for (const c of couriers.data ?? []) map.set(c.provider, (map.get(c.provider) ?? 0) + 1);
    return map;
  }, [couriers.data]);

  return (
    <div className="mt-4 space-y-4">
      <Alert variant="info" title={t("noteTitle")}>
        {t("note")}
      </Alert>
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {query.isPending ? (
        <Skeleton className="h-48" aria-busy="true" />
      ) : (
        <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3" aria-label={t("title")}>
          {(query.data ?? []).map((p: CourierProvider) => (
            <li key={p.code}>
              <Card data-testid="provider-card" data-code={p.code} data-configured={p.isConfigured}>
                <CardContent className="space-y-2 pt-5">
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0">
                      <p className="font-semibold text-strong [overflow-wrap:anywhere]">{p.name}</p>
                      <p className="font-mono text-sm text-muted">{p.code}</p>
                    </div>
                    {p.isConfigured ? (
                      <Badge variant="success">
                        <ShieldCheck className="size-3.5" aria-hidden /> {t("setUp")}
                      </Badge>
                    ) : (
                      <Badge variant="warning">
                        <CircleDashed className="size-3.5" aria-hidden /> {t("notSetUp")}
                      </Badge>
                    )}
                  </div>
                  <p className="text-sm text-muted">{p.isSimulator ? t("simulatorText") : p.isConfigured ? t("configuredText") : t("slotText")}</p>
                  <p className="text-sm text-muted">{t("couriersUsing", { count: used.get(p.code) ?? 0 })}</p>
                </CardContent>
              </Card>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
