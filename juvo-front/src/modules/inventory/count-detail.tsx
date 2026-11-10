"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { ChoiceRow, Switch } from "@/components/ui/form";
import { Alert, Badge, Card, CardContent, CardHeader, CardTitle, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { PageHeader } from "@/components/layout/page-header";
import { useDateTime } from "@/components/data/list-kit";
import { ApiError } from "@/lib/api/client";
import { useReportError } from "@/modules/catalog/api";
import { toNumber } from "@/modules/catalog/format";
import { useCount, useCountAction, useSaveCountLines, type CountAction } from "./api";
import { CountStatusBadge, InventoryTabs, QtyInput, VariantText } from "./common";

type Confirming = "approve" | "cancel" | null;

/**
 * One stocktake. While it is in progress staff type what they counted (the difference shows at once); when it is submitted a manager
 * reviews the differences and approves, which books them as stock movements. The API says which steps this person may take.
 */
export function CountDetail({ id }: { id: string }) {
  const t = useTranslations("inventory.countDetail");
  const tn = useTranslations("nav");
  const tc = useTranslations("inventory.common");
  const dateTime = useDateTime();
  const report = useReportError();
  const query = useCount(id);
  const save = useSaveCountLines(id);
  const run = useCountAction(id);
  const [confirming, setConfirming] = React.useState<Confirming>(null);
  const [onlyDiff, setOnlyDiff] = React.useState(false);
  const [entries, setEntries] = React.useState<Record<string, string>>({});

  const data = query.data;

  // Fill the inputs from the server copy once per version of it (after a save the server copy is the truth).
  const serverKey = data ? `${data.summary.status}|${data.lines.map((l) => `${l.variantId}:${l.countedQuantity ?? ""}`).join(",")}` : null;
  const loaded = React.useRef<string | null>(null);
  React.useEffect(() => {
    if (data && serverKey && loaded.current !== serverKey) {
      loaded.current = serverKey;
      setEntries(Object.fromEntries(data.lines.map((l) => [l.variantId, l.countedQuantity === null || l.countedQuantity === undefined ? "" : String(toNumber(l.countedQuantity))])));
    }
  }, [data, serverKey]);

  const crumbs = [
    { label: tn("items.dashboard"), href: "/admin" },
    { label: tn("items.inventory"), href: "/admin/inventory" },
    { label: t("crumb"), href: "/admin/inventory/counts" },
    { label: data?.summary.number ?? "..." },
  ];

  if (query.isLoading) {
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
        <PageHeader title={t("notFoundTitle")} crumbs={crumbs} />
        <EmptyState
          title={notFound ? t("notFoundTitle") : tc("loadFailed")}
          action={
            <Button asChild variant="secondary">
              <Link href="/admin/inventory/counts">{t("back")}</Link>
            </Button>
          }
        />
      </>
    );
  }

  const { summary } = data;
  const allowed = new Set(data.allowedActions);
  const editable = allowed.has("save");

  const serverValue = (l: (typeof data.lines)[number]) => (l.countedQuantity === null || l.countedQuantity === undefined ? "" : String(toNumber(l.countedQuantity)));
  const dirtyLines = editable ? data.lines.filter((l) => (entries[l.variantId] ?? "") !== serverValue(l)) : [];
  const dirty = dirtyLines.length > 0;

  const liveCounted = (l: (typeof data.lines)[number]): number | null => {
    if (!editable) return l.countedQuantity === null || l.countedQuantity === undefined ? null : toNumber(l.countedQuantity);
    const raw = entries[l.variantId];
    return raw === undefined || raw === "" ? null : Number(raw);
  };
  const rows = data.lines.map((l) => {
    const counted = liveCounted(l);
    return { line: l, counted, difference: counted === null ? null : counted - toNumber(l.expectedQuantity) };
  });
  const countedCount = rows.filter((r) => r.counted !== null).length;
  const differing = rows.filter((r) => r.difference !== null && r.difference !== 0);
  const net = differing.reduce((sum, r) => sum + (r.difference ?? 0), 0);
  const shown = onlyDiff ? differing : rows;

  const saveLines = async (): Promise<boolean> => {
    try {
      await save.mutateAsync(dirtyLines.map((l) => ({ variantId: l.variantId, countedQuantity: entries[l.variantId] === "" ? null : Number(entries[l.variantId]) })));
      return true;
    } catch (e) {
      report(e);
      return false;
    }
  };

  const onSave = async () => {
    if (await saveLines()) toast.success(t("saved"));
  };

  const act = async (action: CountAction) => {
    if (action === "submit" && dirty && !(await saveLines())) return;
    try {
      await run.mutateAsync(action);
      toast.success(t(`done.${action}`));
    } catch (e) {
      report(e);
    }
    setConfirming(null);
  };

  const busy = save.isPending || run.isPending;

  return (
    <>
      <PageHeader
        title={summary.number}
        description={t("description", { branch: summary.branchCode })}
        crumbs={crumbs}
        actions={
          <>
            <CountStatusBadge status={summary.status} />
            {editable ? (
              <Button variant="secondary" onClick={() => void onSave()} loading={save.isPending} disabled={!dirty}>
                {t("actions.save")}
              </Button>
            ) : null}
            {allowed.has("submit") ? (
              <Button onClick={() => void act("submit")} loading={busy} disabled={countedCount === 0}>
                {t("actions.submit")}
              </Button>
            ) : null}
            {allowed.has("approve") ? <Button onClick={() => setConfirming("approve")}>{t("actions.approve")}</Button> : null}
            {allowed.has("reopen") ? (
              <Button variant="secondary" onClick={() => void act("reopen")} loading={run.isPending}>
                {t("actions.reopen")}
              </Button>
            ) : null}
            {allowed.has("cancel") ? (
              <Button variant="secondary" onClick={() => setConfirming("cancel")}>
                {t("actions.cancel")}
              </Button>
            ) : null}
          </>
        }
      />
      <InventoryTabs />

      {summary.note ? <p className="mb-4 text-muted [overflow-wrap:anywhere]">{t("noteLine", { note: summary.note })}</p> : null}
      {summary.status === "Submitted" ? <Alert variant="info" className="mb-4">{t("reviewHint")}</Alert> : null}
      {summary.status === "Approved" ? <Alert variant="success" className="mb-4">{t("approvedHint", { date: dateTime(summary.closedAt) })}</Alert> : null}

      <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-4">
        <SummaryCard label={t("summary.lines")} value={rows.length} />
        <SummaryCard label={t("summary.counted")} value={`${countedCount} / ${rows.length}`} />
        <SummaryCard label={t("summary.differing")} value={differing.length} />
        <SummaryCard label={t("summary.net")} value={`${net > 0 ? "+" : ""}${net}`} />
      </div>

      <Card>
        <CardHeader className="gap-3 sm:flex-row sm:items-center sm:justify-between">
          <CardTitle>{t("linesTitle")}</CardTitle>
          <ChoiceRow htmlFor="cd-only-diff">
            <Switch id="cd-only-diff" checked={onlyDiff} onCheckedChange={setOnlyDiff} />
            {t("onlyDifferences")}
          </ChoiceRow>
        </CardHeader>
        <CardContent>
          {editable ? <p className="mb-3 text-sm text-muted">{t("editHint")}</p> : null}
          <div className="hidden grid-cols-[minmax(0,1fr)_6rem_8rem_7rem] gap-3 border-b border-line pb-2 text-sm font-semibold text-muted sm:grid" aria-hidden>
            <span>{t("columns.product")}</span>
            <span className="text-end">{t("columns.expected")}</span>
            <span>{t("columns.counted")}</span>
            <span className="text-end">{t("columns.difference")}</span>
          </div>
          {shown.length === 0 ? <p className="py-6 text-center text-muted">{onlyDiff ? t("noDifferences") : t("noLines")}</p> : null}
          <ul className="divide-y divide-line" aria-label={t("linesTitle")}>
            {shown.map(({ line, difference }) => (
              <li key={line.variantId} className="grid gap-2 py-3 sm:grid-cols-[minmax(0,1fr)_6rem_8rem_7rem] sm:items-center sm:gap-3">
                <div className="min-w-0">
                  <Link href={`/admin/products/${line.productId}`} className="inline-flex min-h-11 items-center rounded-control font-semibold text-strong hover:text-primary hover:underline">
                    <span className="[overflow-wrap:anywhere]">{line.productName}</span>
                  </Link>
                  <VariantText sku={line.sku} colorName={line.colorName} size={line.size} />
                </div>
                <p className="flex items-center justify-between sm:block sm:text-end">
                  <span className="text-sm text-muted sm:sr-only">{t("columns.expected")}</span>
                  <span className="tabular-nums">{toNumber(line.expectedQuantity)}</span>
                </p>
                <div>
                  {editable ? (
                    <QtyInput
                      aria-label={t("countedFor", { sku: line.sku })}
                      value={entries[line.variantId] ?? ""}
                      onValueChange={(v) => setEntries((old) => ({ ...old, [line.variantId]: v }))}
                      disabled={busy}
                    />
                  ) : (
                    <p className="flex items-center justify-between sm:block">
                      <span className="text-sm text-muted sm:sr-only">{t("columns.counted")}</span>
                      <span className="tabular-nums">{line.countedQuantity === null || line.countedQuantity === undefined ? t("notCounted") : toNumber(line.countedQuantity)}</span>
                    </p>
                  )}
                </div>
                <p className="flex items-center justify-between sm:justify-end">
                  <span className="text-sm text-muted sm:sr-only">{t("columns.difference")}</span>
                  {difference === null ? (
                    <span className="text-muted">-</span>
                  ) : difference === 0 ? (
                    <Badge variant="success">{t("same")}</Badge>
                  ) : (
                    <span className={`font-semibold tabular-nums ${difference < 0 ? "text-danger" : "text-success"}`}>
                      {difference > 0 ? "+" : ""}
                      {difference}
                    </span>
                  )}
                </p>
              </li>
            ))}
          </ul>
        </CardContent>
      </Card>

      <ConfirmDialog
        open={confirming === "approve"}
        onOpenChange={(open) => !open && setConfirming(null)}
        title={t("confirm.approve.title")}
        description={t("confirm.approve.description", { lines: differing.length, net })}
        confirmLabel={t("confirm.approve.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={run.isPending}
        onConfirm={() => act("approve")}
      />
      <ConfirmDialog
        open={confirming === "cancel"}
        onOpenChange={(open) => !open && setConfirming(null)}
        title={t("confirm.cancel.title")}
        description={t("confirm.cancel.description")}
        confirmLabel={t("confirm.cancel.confirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={run.isPending}
        onConfirm={() => act("cancel")}
      />
    </>
  );
}

function SummaryCard({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <Card className="p-4">
      <p className="text-sm text-muted">{label}</p>
      <p className="mt-1 text-2xl font-semibold tabular-nums text-strong">{value}</p>
    </Card>
  );
}
