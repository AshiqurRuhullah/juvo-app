"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Ban, CircleCheck, Phone, Plus, ShieldAlert, Trash2, WalletCards } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Field, Input, Label, Switch, Textarea } from "@/components/ui/form";
import { Select } from "@/components/ui/select";
import { Tabs } from "@/components/ui/tabs";
import { Alert, Badge, EmptyState, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, useDateTime, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import {
  BLACKLIST_KINDS,
  HELD_STATES,
  RULE_ACTIONS,
  useAddBlacklist,
  useBlacklist,
  useFraudDecision,
  useFraudReviews,
  useFraudRules,
  useOrderErrorText,
  useRemoveBlacklist,
  useUpdateFraudRule,
  type Blacklist,
  type BlacklistKind,
  type CustomerStats,
  type FraudAction,
  type FraudRule,
  type FraudState,
  type ReviewItem,
  type RuleAction,
} from "./api";
import { FraudBadge, OrderStatusBadge, RiskChips, useMethodLabel } from "./common";
import { useOrderPermissions } from "./permissions";

/** Counts and score of a customer's past orders, as a small panel. */
export function StatsPanel({ stats }: { stats: CustomerStats }) {
  const t = useTranslations("orders.fraud.stats");
  const money = useMoney();
  const dateTime = useDateTime();
  const rows: [string, string][] = [
    ["totalOrders", String(toNumber(stats.totalOrders))],
    ["delivered", String(toNumber(stats.delivered))],
    ["cancelled", String(toNumber(stats.cancelled))],
    ["returnedOrFailed", String(toNumber(stats.returnedOrFailed))],
    ["open", String(toNumber(stats.open))],
    ["spent", money(stats.totalSpent)],
    ["first", stats.firstOrderAt ? dateTime(stats.firstOrderAt) : "-"],
    ["score", String(toNumber(stats.score))],
  ];
  return (
    <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm sm:grid-cols-4" data-testid="stats-panel">
      {rows.map(([key, value]) => (
        <div key={key}>
          <dt className="text-muted">{t(key)}</dt>
          <dd className="font-semibold tabular-nums text-strong">{value}</dd>
        </div>
      ))}
    </dl>
  );
}

const ACTION_ICON = { Approve: CircleCheck, RequestAdvance: WalletCards, Call: Phone, Reject: Ban } as const;
const ACTIONS: FraudAction[] = ["Approve", "RequestAdvance", "Call", "Reject"];

function ReviewQueue() {
  const t = useTranslations("orders.fraud.queue");
  const ts = useTranslations("orders.status");
  const tf = useTranslations("orders.fraud");
  const tc = useTranslations("orders.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const methodLabel = useMethodLabel();
  const errorText = useOrderErrorText();
  const perms = useOrderPermissions();
  const [state, setState] = React.useState<FraudState | undefined>(undefined);
  const [page, setPage] = React.useState(1);
  const query = useFraudReviews({ page, pageSize: 10, state });
  const decide = useFraudDecision();
  const [target, setTarget] = React.useState<{ item: ReviewItem; action: FraudAction } | null>(null);
  const [note, setNote] = React.useState("");
  const [noteError, setNoteError] = React.useState<string | null>(null);
  const items = query.data?.items ?? [];
  const pages = Math.max(1, Math.ceil(toNumber(query.data?.total) / 10));

  const close = () => {
    setTarget(null);
    setNote("");
    setNoteError(null);
  };
  const submit = async () => {
    if (!target) return;
    if (target.action === "Reject" && !note.trim()) return setNoteError(tf("noteRequired"));
    try {
      await decide.mutateAsync({ orderId: target.item.orderId, action: target.action, note: note.trim() || null });
      toast.success(tf(`done.${target.action}`, { number: target.item.number }));
      close();
    } catch (failure) {
      toast.error(errorText(failure));
      close();
    }
  };

  return (
    <div className="space-y-4">
      <div className="max-w-xs">
        <FilterSelect
          value={state}
          onChange={(v) => (setState(v as FraudState | undefined), setPage(1))}
          options={HELD_STATES.map((s) => ({ value: s, label: ts(`fraud.${s}`) }))}
          allLabel={t("anyState")}
          label={t("state")}
          searchPlaceholder={tc("search")}
          emptyText={tc("noMatch")}
        />
      </div>
      {query.isError ? <Alert variant="danger">{errorText(query.error)}</Alert> : null}
      {query.isLoading ? <Skeleton className="h-48" aria-busy /> : null}
      {!query.isLoading && items.length === 0 ? <EmptyState icon={<ShieldAlert aria-hidden />} title={t("empty")} description={t("emptyHelp")} /> : null}

      <ul className="space-y-4" aria-label={t("title")} data-testid="review-queue">
        {items.map((item) => (
          <li key={item.orderId} className="space-y-4 rounded-card border border-line bg-card p-4 sm:p-5" data-testid="review-item" data-number={item.number}>
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div className="min-w-0 space-y-1">
                <Link href={`/admin/orders/${item.orderId}`} className="inline-flex min-h-11 items-center rounded-control font-mono text-lg font-semibold text-strong hover:text-primary hover:underline">
                  {item.number}
                </Link>
                <p className="text-sm text-muted [overflow-wrap:anywhere]">
                  {item.customerName ?? "-"} · {item.customerMobile}
                </p>
                <p className="text-sm text-muted">
                  {dateTime(item.placedAt)} · {methodLabel(item.paymentMethod)}
                </p>
              </div>
              <div className="space-y-1 text-end">
                <p className="text-lg font-semibold text-strong">{money(item.grandTotal)}</p>
                <p className="flex flex-wrap items-center justify-end gap-1.5">
                  <FraudBadge state={item.state} />
                  <OrderStatusBadge status={item.orderStatus} />
                </p>
                <p className="text-sm text-muted" data-testid="risk-score">
                  {t("score")}: <strong className="text-strong">{toNumber(item.riskScore)}</strong>
                </p>
              </div>
            </div>
            <RiskChips flags={item.flags} />
            <div className="border-t border-line pt-3">
              <h3 className="mb-2 text-sm font-semibold text-strong">{t("customer")}</h3>
              <StatsPanel stats={item.customerStats} />
            </div>
            {perms.fraudDecide ? (
              <div className="flex flex-wrap gap-2 border-t border-line pt-3" role="group" aria-label={t("decide")}>
                {ACTIONS.map((action) => {
                  const Icon = ACTION_ICON[action];
                  return (
                    <Button key={action} variant={action === "Reject" ? "danger" : action === "Approve" ? "primary" : "secondary"} onClick={() => setTarget({ item, action })} data-testid={`decide-${action}`}>
                      <Icon aria-hidden /> {tf(`action.${action}`)}
                    </Button>
                  );
                })}
              </div>
            ) : null}
          </li>
        ))}
      </ul>

      {pages > 1 ? (
        <div className="flex items-center justify-between gap-2">
          <Button variant="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>
            {tc("previous")}
          </Button>
          <span className="text-sm text-muted">{tc("pageOf", { page, pages })}</span>
          <Button variant="secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>
            {tc("next")}
          </Button>
        </div>
      ) : null}

      <ConfirmDialog
        open={!!target}
        onOpenChange={(open) => (open ? undefined : close())}
        title={target ? tf(`dialog.${target.action}.title`, { number: target.item.number }) : ""}
        description={target ? tf(`dialog.${target.action}.text`) : ""}
        confirmLabel={target ? tf(`action.${target.action}`) : ""}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive={target?.action === "Reject"}
        loading={decide.isPending}
        onConfirm={submit}
      >
        <Field label={tf("note")} htmlFor="fraud-note" required={target?.action === "Reject"} error={noteError ?? undefined}>
          <Textarea id="fraud-note" rows={3} value={note} onChange={(e) => setNote(e.target.value)} aria-invalid={!!noteError} />
        </Field>
      </ConfirmDialog>
    </div>
  );
}

function BlacklistTab() {
  const t = useTranslations("orders.fraud.blacklist");
  const tc = useTranslations("orders.common");
  const labels = useTableLabels();
  const dateTime = useDateTime();
  const errorText = useOrderErrorText();
  const perms = useOrderPermissions();
  const [kind, setKind] = React.useState<BlacklistKind | undefined>(undefined);
  const [page, setPage] = React.useState(1);
  const query = useBlacklist({ page, pageSize: 20, kind });
  const add = useAddBlacklist();
  const remove = useRemoveBlacklist();
  const [newKind, setNewKind] = React.useState<BlacklistKind>("Mobile");
  const [value, setValue] = React.useState("");
  const [note, setNote] = React.useState("");
  const [valueError, setValueError] = React.useState<string | null>(null);
  const [removing, setRemoving] = React.useState<Blacklist | null>(null);

  const columns = React.useMemo<ColumnDef<Blacklist, unknown>[]>(
    () => [
      { id: "value", header: t("columns.value"), enableSorting: false, meta: { primary: true }, cell: ({ row }) => <span className="font-mono [overflow-wrap:anywhere]">{row.original.value}</span> },
      { id: "kind", header: t("columns.kind"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <Badge variant="outline">{t(`kind.${row.original.kind}`)}</Badge> },
      { id: "note", header: t("columns.note"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="text-muted [overflow-wrap:anywhere]">{row.original.note ?? "-"}</span> },
      { id: "created", header: t("columns.created"), enableSorting: false, meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}{row.original.createdByName ? ` · ${row.original.createdByName}` : ""}</span> },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) =>
          perms.blacklistDelete ? (
            <Button variant="secondary" onClick={() => setRemoving(row.original)} aria-label={t("removeEntry", { value: row.original.value })}>
              <Trash2 aria-hidden /> {t("remove")}
            </Button>
          ) : null,
      },
    ],
    [t, dateTime, perms.blacklistDelete],
  );

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!value.trim()) return setValueError(t("valueRequired"));
    setValueError(null);
    try {
      await add.mutateAsync({ kind: newKind, value: value.trim(), note: note.trim() || null });
      toast.success(t("added"));
      setValue("");
      setNote("");
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  return (
    <div className="space-y-5">
      {perms.blacklistCreate ? (
        <form onSubmit={submit} noValidate className="grid gap-3 rounded-card border border-line bg-card p-4 sm:grid-cols-2 lg:grid-cols-[10rem_1fr_1fr_auto] lg:items-start" aria-label={t("add")}>
          <Field label={t("columns.kind")} htmlFor="bl-kind">
            <Select id="bl-kind" value={newKind} onValueChange={(v) => setNewKind(v as BlacklistKind)} options={BLACKLIST_KINDS.map((k) => ({ value: k, label: t(`kind.${k}`) }))} placeholder={tc("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} searchable={false} />
          </Field>
          <Field label={t("columns.value")} htmlFor="bl-value" required error={valueError ?? undefined} hint={t(`hint.${newKind}`)}>
            <Input id="bl-value" value={value} onChange={(e) => setValue(e.target.value)} aria-invalid={!!valueError} autoComplete="off" />
          </Field>
          <Field label={t("columns.note")} htmlFor="bl-note">
            <Input id="bl-note" value={note} onChange={(e) => setNote(e.target.value)} autoComplete="off" />
          </Field>
          <div className="lg:pt-[1.625rem]">
            <Button type="submit" loading={add.isPending} data-testid="bl-add" className="w-full">
              <Plus aria-hidden /> {t("add")}
            </Button>
          </div>
        </form>
      ) : null}

      <div className="max-w-xs">
        <FilterSelect value={kind} onChange={(v) => (setKind(v as BlacklistKind | undefined), setPage(1))} options={BLACKLIST_KINDS.map((k) => ({ value: k, label: t(`kind.${k}`) }))} allLabel={t("anyKind")} label={t("columns.kind")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
      </div>
      {query.isError ? <Alert variant="danger">{errorText(query.error)}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={query.data?.items ?? []} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} page={page} pageSize={20} total={toNumber(query.data?.total)} onPageChange={setPage} />

      <ConfirmDialog
        open={!!removing}
        onOpenChange={(open) => (open ? undefined : setRemoving(null))}
        title={t("removeTitle")}
        description={removing ? t("removeText", { value: removing.value }) : ""}
        confirmLabel={t("remove")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={remove.isPending}
        onConfirm={async () => {
          if (!removing) return;
          try {
            await remove.mutateAsync(removing.id);
            toast.success(t("removed"));
          } catch (failure) {
            toast.error(errorText(failure));
          }
          setRemoving(null);
        }}
      />
    </div>
  );
}

function RuleRow({ rule, canEdit }: { rule: FraudRule; canEdit: boolean }) {
  const t = useTranslations("orders.fraud.rules");
  const tc = useTranslations("orders.common");
  const errorText = useOrderErrorText();
  const update = useUpdateFraudRule();
  const [enabled, setEnabled] = React.useState(rule.enabled);
  const [threshold, setThreshold] = React.useState(String(toNumber(rule.threshold)));
  const [action, setAction] = React.useState<RuleAction>(rule.action);
  const dirty = enabled !== rule.enabled || Number(threshold) !== toNumber(rule.threshold) || action !== rule.action;

  const save = async () => {
    const value = Number(threshold);
    if (!Number.isFinite(value) || value < 0) return toast.error(t("thresholdInvalid"));
    try {
      await update.mutateAsync({ code: rule.code, enabled, threshold: value, action });
      toast.success(t("saved", { code: rule.code }));
    } catch (failure) {
      toast.error(errorText(failure));
    }
  };

  return (
    <li className="space-y-3 rounded-card border border-line bg-card p-4" data-testid="rule" data-code={rule.code}>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="font-mono text-sm font-semibold text-strong">{rule.code}</p>
          <p className="text-strong [overflow-wrap:anywhere]">{rule.description}</p>
        </div>
        <div className="flex min-h-11 items-center gap-3">
          <Label htmlFor={`rule-${rule.code}-enabled`} className="flex min-h-11 cursor-pointer items-center">
            {enabled ? t("enabled") : t("disabled")}
          </Label>
          <Switch id={`rule-${rule.code}-enabled`} checked={enabled} onCheckedChange={setEnabled} disabled={!canEdit} />
        </div>
      </div>
      <div className="grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-end">
        <Field label={t("threshold")} htmlFor={`rule-${rule.code}-threshold`} hint={rule.thresholdMeaning}>
          <Input id={`rule-${rule.code}-threshold`} inputMode="decimal" value={threshold} onChange={(e) => setThreshold(e.target.value.replace(/[^\d.]/g, ""))} disabled={!canEdit} />
        </Field>
        <Field label={t("action")} htmlFor={`rule-${rule.code}-action`}>
          <Select id={`rule-${rule.code}-action`} value={action} onValueChange={(v) => setAction(v as RuleAction)} options={RULE_ACTIONS.map((a) => ({ value: a, label: t(`actions.${a}`) }))} placeholder={tc("choose")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} disabled={!canEdit} searchable={false} />
        </Field>
        {canEdit ? (
          <Button onClick={() => void save()} loading={update.isPending} disabled={!dirty} data-testid="rule-save" className="sm:mb-[1.625rem]">
            {t("save")}
          </Button>
        ) : null}
      </div>
    </li>
  );
}

function RulesTab() {
  const t = useTranslations("orders.fraud.rules");
  const errorText = useOrderErrorText();
  const perms = useOrderPermissions();
  const query = useFraudRules();
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted">{t("help")}</p>
      {query.isError ? <Alert variant="danger">{errorText(query.error)}</Alert> : null}
      {query.isLoading ? <Skeleton className="h-48" aria-busy /> : null}
      <ul className="space-y-3" aria-label={t("title")}>
        {(query.data ?? []).map((rule) => (
          <RuleRow key={`${rule.code}-${rule.enabled}-${rule.threshold}-${rule.action}`} rule={rule} canEdit={perms.ruleEdit} />
        ))}
      </ul>
    </div>
  );
}

/** `/admin/fraud`: the review queue, the blacklist and the rules, each shown only to people who may see it. */
export function FraudView() {
  const t = useTranslations("orders.fraud");
  const perms = useOrderPermissions();
  const tabs = [
    ...(perms.fraudView ? [{ id: "queue", label: t("tabs.queue") }] : []),
    ...(perms.blacklistView ? [{ id: "blacklist", label: t("tabs.blacklist") }] : []),
    ...(perms.ruleView ? [{ id: "rules", label: t("tabs.rules") }] : []),
  ];
  const [tab, setTab] = React.useState(tabs[0]?.id ?? "queue");
  if (tabs.length === 0) return <Alert variant="warning">{t("noAccess")}</Alert>;
  const current = tabs.some((x) => x.id === tab) ? tab : tabs[0]!.id;
  return (
    <Tabs tabs={tabs} value={current} onValueChange={setTab} label={t("title")}>
      {(active) => (active === "queue" ? <ReviewQueue /> : active === "blacklist" ? <BlacklistTab /> : <RulesTab />)}
    </Tabs>
  );
}
