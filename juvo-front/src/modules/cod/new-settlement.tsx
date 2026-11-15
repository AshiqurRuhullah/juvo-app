"use client";

import * as React from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { PackageOpen } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Checkbox, Field, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Alert, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { useSession } from "@/components/providers/session-provider";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { canInBranch } from "@/lib/auth/permissions";
import { useCouriers, useCreateSettlement, type Settlement, type ShipmentListItem } from "@/modules/couriers/api";
import { useMoney2 } from "./common";
import { NOTE_MAX, pendingAmount, selectionTotals, shipmentIdsToSend } from "./logic";
import { usePendingParcels } from "./queries";

const DEFAULT_BRANCH = "__default";

type Row ={ id: string; orderId: string; orderNumber: string; consignmentId: string; deliveredAt: string | null; amount: number };

/** `/admin/cod/new`: choose a courier and branch, tick the delivered cash parcels waiting for a batch, and make the batch. */
export function NewSettlement() {
  const t = useTranslations("cod.new");
  const tc = useTranslations("cod.common");
  const tn = useTranslations("nav");
  const router = useRouter();
  const { me } = useSession();
  const problemMessage = useProblemMessage();
  const couriers = useCouriers();
  const create = useCreateSettlement();

  // Only branches where this person may settle are offered. The active branch is the default; in "All branches" mode the shop's
  // default branch is meant (the API picks it when no branch is sent).
  const activeBranch = me.activeBranchId && !me.isAllBranches ? me.activeBranchId : undefined;
  const branchOptions = React.useMemo(() => {
    const own = me.branches.filter((b) => b.isActive && canInBranch(me, "couriers.cod.settle", b.id)).map((b) => ({ value: b.id, label: `${b.code} · ${b.nameEn}` }));
    return activeBranch ? own : [{ value: DEFAULT_BRANCH, label: t("defaultBranch") }, ...own];
  }, [me, activeBranch, t]);
  const [branchChoice, setBranchChoice] = React.useState<string>(() => activeBranch ?? DEFAULT_BRANCH);
  const branchId = branchChoice === DEFAULT_BRANCH ? undefined : branchChoice;
  const [courierId, setCourierId] = React.useState<string | undefined>(undefined);
  const [excluded, setExcluded] = React.useState<ReadonlySet<string>>(new Set());
  const [note, setNote] = React.useState("");
  const [failure, setFailure] = React.useState<string | null>(null);

  const courierOptions = React.useMemo(() => (couriers.data ?? []).filter((c) => c.codSupported).map((c) => ({ value: c.id, label: c.name })), [couriers.data]);
  // One courier: nothing to choose, take it.
  const chosenCourier = courierId ?? (courierOptions.length === 1 ? courierOptions[0]!.value : undefined);

  const pending = usePendingParcels(chosenCourier, branchId);
  const rows = React.useMemo<Row[]>(
    () =>
      (pending.data ?? []).map((p: ShipmentListItem) => ({ id: p.id, orderId: p.orderId, orderNumber: p.orderNumber, consignmentId: p.consignmentId, deliveredAt: p.deliveredAt, amount: pendingAmount(p) })),
    [pending.data],
  );
  const totals = selectionTotals(rows, excluded);

  const toggle = React.useCallback((id: string, on: boolean) => {
    setExcluded((old) => {
      const next = new Set(old);
      if (on) next.delete(id);
      else next.add(id);
      return next;
    });
  }, []);
  const selectAll = (on: boolean) => setExcluded(on ? new Set() : new Set(rows.map((r) => r.id)));

  const submit = async () => {
    if (!chosenCourier || totals.count === 0) return;
    setFailure(null);
    try {
      const made = (await create.mutateAsync({ courierId: chosenCourier, branchId, shipmentIds: shipmentIdsToSend(rows, excluded), note: note.trim() || null })) as Settlement;
      router.push(`/admin/cod/${made.id}`);
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
      // The waiting list may have changed (another person made a batch): show what is waiting now.
      void pending.refetch();
      setExcluded(new Set());
    }
  };

  const crumbs = [{ label: tn("items.dashboard"), href: "/admin" }, { label: tn("items.codSettlements"), href: "/admin/cod" }, { label: t("title") }];

  return (
    <>
      <PageHeader title={t("title")} description={t("subtitle")} crumbs={crumbs} />
      <div className="space-y-6">
        <Card>
          <CardHeader>
            <CardTitle>{t("whichParcels")}</CardTitle>
          </CardHeader>
          <CardContent className="grid gap-4 sm:grid-cols-2">
            <Field label={t("courier")} htmlFor="cod-courier">
              {couriers.isPending ? (
                <Skeleton className="h-12" aria-busy="true" />
              ) : (
                <Select
                  id="cod-courier"
                  value={chosenCourier}
                  onValueChange={(v) => {
                    setCourierId(v);
                    setExcluded(new Set());
                    setFailure(null);
                  }}
                  options={courierOptions}
                  placeholder={t("chooseCourier")}
                  searchPlaceholder={tc("search")}
                  emptyText={tc("noMatch")}
                />
              )}
            </Field>
            <Field label={t("branch")} htmlFor="cod-branch">
              <Select
                id="cod-branch"
                value={branchChoice}
                onValueChange={(v) => {
                  setBranchChoice(v);
                  setExcluded(new Set());
                  setFailure(null);
                }}
                options={branchOptions}
                placeholder={t("chooseBranch")}
                searchPlaceholder={tc("search")}
                emptyText={tc("noMatch")}
              />
            </Field>
            {couriers.isError ? (
              <Alert variant="warning" className="sm:col-span-2">
                {t("couriersFailed")}
              </Alert>
            ) : null}
          </CardContent>
        </Card>

        {/* Kept outside the parcel list: after a refusal the list is read again and may be empty, the reason must stay visible. */}
        {failure ? (
          <Alert variant="danger" data-testid="create-error">
            {failure}
          </Alert>
        ) : null}

        {!chosenCourier ? (
          <Card>
            <CardContent>
              <EmptyState icon={<PackageOpen />} title={t("pickCourierTitle")} description={t("pickCourierHint")} />
            </CardContent>
          </Card>
        ) : pending.isPending ? (
          <Skeleton className="h-56" aria-busy="true" />
        ) : pending.isError ? (
          <Alert variant="danger">{tc("loadFailed")}</Alert>
        ) : rows.length === 0 ? (
          <Card>
            <CardContent>
              <EmptyState
                icon={<PackageOpen />}
                title={t("nothingTitle")}
                description={t("nothingHint")}
                action={
                  <Button asChild variant="secondary">
                    <Link href="/admin/shipments">{t("toParcels")}</Link>
                  </Button>
                }
              />
            </CardContent>
          </Card>
        ) : (
          <Card>
            <CardHeader className="flex-row flex-wrap items-center justify-between gap-3">
              <CardTitle>{t("waiting", { count: rows.length })}</CardTitle>
              <label className="inline-flex min-h-11 cursor-pointer items-center gap-3 font-medium text-strong">
                <Checkbox
                  aria-label={t("selectAll")}
                  checked={excluded.size === 0 ? true : totals.count === 0 ? false : "indeterminate"}
                  onCheckedChange={(v) => selectAll(v === true)}
                  data-testid="select-all"
                />
                {t("selectAll")}
              </label>
            </CardHeader>
            <CardContent className="space-y-4">
              <ul className="divide-y divide-line" aria-label={t("waiting", { count: rows.length })}>
                {rows.map((row) => (
                  <PendingRow key={row.id} row={row} checked={!excluded.has(row.id)} onToggle={toggle} />
                ))}
              </ul>

              <div className="rounded-card border border-line bg-sunken p-4" aria-live="polite" data-testid="selection-summary">
                <p className="text-sm text-muted">{t("selected", { count: totals.count, total: rows.length })}</p>
                <p className="text-2xl font-bold tabular-nums text-strong" data-testid="selection-total">
                  <Money value={totals.total} />
                </p>
              </div>

              <Field label={t("note")} htmlFor="cod-note" hint={t("noteHint")}>
                <Textarea id="cod-note" rows={2} maxLength={NOTE_MAX} value={note} onChange={(e) => setNote(e.target.value)} />
              </Field>

              <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
                <Button asChild variant="secondary">
                  <Link href="/admin/cod">{tc("cancel")}</Link>
                </Button>
                <Button onClick={() => void submit()} loading={create.isPending} disabled={totals.count === 0} data-testid="create-batch">
                  {t("create", { count: totals.count })}
                </Button>
              </div>
            </CardContent>
          </Card>
        )}
      </div>
    </>
  );
}

function Money({ value }: { value: number }) {
  const money = useMoney2();
  return <>{money(value)}</>;
}

/** One waiting parcel. Memoised: ticking one parcel does not redraw the others. */
const PendingRow = React.memo(function PendingRow({ row, checked, onToggle }: { row: Row; checked: boolean; onToggle: (id: string, on: boolean) => void }) {
  const t = useTranslations("cod.new");
  const money = useMoney2();
  const dateTime = useDateTime();
  return (
    <li className="flex items-start gap-3 py-2" data-testid="pending-row" data-consignment={row.consignmentId}>
      <label className="-my-1 flex min-h-11 flex-1 cursor-pointer items-start gap-3 py-1">
        <Checkbox className="mt-1" aria-label={t("selectRow", { consignment: row.consignmentId })} checked={checked} onCheckedChange={(v) => onToggle(row.id, v === true)} />
        <span className="grid min-w-0 flex-1 gap-x-4 gap-y-0.5 sm:grid-cols-[minmax(0,1fr)_auto_auto] sm:items-center">
          <span className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]">{row.consignmentId}</span>
          <span className="text-sm text-muted">{t("deliveredOn", { date: dateTime(row.deliveredAt) })}</span>
          <span className="font-semibold tabular-nums text-strong sm:text-end">{money(row.amount)}</span>
        </span>
      </label>
      <Link href={`/admin/orders/${row.orderId}`} className="inline-flex min-h-11 min-w-11 items-center justify-center rounded-control px-2 font-mono text-sm text-primary hover:underline" aria-label={t("openOrder", { number: row.orderNumber })}>
        {row.orderNumber}
      </Link>
    </li>
  );
});
