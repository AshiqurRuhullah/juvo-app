"use client";

import * as React from "react";
import Link from "next/link";
import { useTranslations } from "next-intl";
import { Button } from "@/components/ui/button";
import { Field, Input } from "@/components/ui/form";
import { Alert, Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { ApiError } from "@/lib/api/client";
import { useProblemMessage } from "@/lib/api/use-problem-message";
import { useReconcileSettlement, type Settlement, type SettlementItem } from "@/modules/couriers/api";
import { Difference, useMoney2 } from "./common";
import { NOTE_MAX, batchTotals, checkLine, lineErrors, num, toReconcileLines, type LineInput } from "./logic";

/**
 * What is typed, kept outside React state: a keystroke in one row updates this store and redraws that row and the totals only
 * (rows are memoised and own their own text), so a batch of many parcels stays responsive.
 */
type Entry = { received: string; note: string };
function createStore(items: readonly SettlementItem[]) {
  const values = new Map<string, Entry>();
  for (const item of items) {
    const expected = num(item.expected);
    // A batch reconciled before starts from what was entered then; a fresh one starts from "paid in full".
    values.set(item.shipmentId, { received: (item.received !== null ? num(item.received) : expected).toFixed(2), note: item.note ?? "" });
  }
  let version = 0;
  const listeners = new Set<() => void>();
  return {
    values,
    set(id: string, entry: Entry) {
      values.set(id, entry);
      version += 1;
      listeners.forEach((l) => l());
    },
    subscribe(listener: () => void) {
      listeners.add(listener);
      return () => void listeners.delete(listener);
    },
    snapshot: () => version,
  };
}
type Store = ReturnType<typeof createStore>;

const toInputs = (items: readonly SettlementItem[], store: Store): LineInput[] =>
  items.map((i) => ({ shipmentId: i.shipmentId, expected: num(i.expected), received: store.values.get(i.shipmentId)?.received ?? "", note: store.values.get(i.shipmentId)?.note ?? "" }));

/** The reconcile form of an open or reconciled batch: one received amount per parcel, live differences, a note for every short or over parcel. */
export function ReconcileForm({ settlement }: { settlement: Settlement }) {
  const t = useTranslations("cod.reconcile");
  const problemMessage = useProblemMessage();
  const run = useReconcileSettlement();
  const items = settlement.items;
  const [store] = React.useState(() => createStore(items));
  const [attempted, setAttempted] = React.useState(false);
  const [failure, setFailure] = React.useState<string | null>(null);
  const [problems, setProblems] = React.useState<{ amount: number; note: number }>({ amount: 0, note: 0 });

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setFailure(null);
    const inputs = toInputs(items, store);
    const errors = lineErrors(inputs);
    const ids = Object.keys(errors);
    setAttempted(true);
    if (ids.length > 0) {
      setProblems({ amount: ids.filter((id) => errors[id] === "amount").length, note: ids.filter((id) => errors[id] !== "amount").length });
      const first = items.find((i) => errors[i.shipmentId]);
      if (first) document.getElementById(errors[first.shipmentId] === "amount" ? `recv-${first.shipmentId}` : `note-${first.shipmentId}`)?.focus();
      return;
    }
    setProblems({ amount: 0, note: 0 });
    try {
      await run.mutateAsync({ id: settlement.id, lines: toReconcileLines(inputs) });
      toast.success(t("saved"));
    } catch (e) {
      setFailure(problemMessage(e instanceof ApiError ? e.problem : null));
    }
  };

  return (
    <Card data-testid="reconcile-form">
      <CardHeader>
        <CardTitle>{t("title")}</CardTitle>
        <CardDescription>{t("hint")}</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={(e) => void submit(e)} noValidate className="space-y-5">
          <ul className="space-y-3" aria-label={t("title")}>
            {items.map((item) => (
              <ReconcileRow key={item.shipmentId} item={item} store={store} attempted={attempted} />
            ))}
          </ul>
          <Totals items={items} store={store} />
          {attempted && (problems.amount > 0 || problems.note > 0) ? (
            <Alert variant="danger" data-testid="reconcile-problems">
              {[problems.amount > 0 ? t("problemAmount", { count: problems.amount }) : null, problems.note > 0 ? t("problemNote", { count: problems.note }) : null].filter(Boolean).join(" ")}
            </Alert>
          ) : null}
          {failure ? <Alert variant="danger" data-testid="reconcile-error">{failure}</Alert> : null}
          <div className="flex justify-end">
            <Button type="submit" loading={run.isPending} data-testid="reconcile-save">
              {t("save")}
            </Button>
          </div>
        </form>
      </CardContent>
    </Card>
  );
}

/** Worked totals: what was expected, what was received, the difference and how many parcels are short or over. Redraws on every keystroke, nothing else does. */
function Totals({ items, store }: { items: readonly SettlementItem[]; store: Store }) {
  const t = useTranslations("cod.reconcile");
  const money = useMoney2();
  React.useSyncExternalStore(store.subscribe, store.snapshot, store.snapshot);
  const totals = batchTotals(toInputs(items, store));
  return (
    <div className="rounded-card border border-line bg-sunken p-4" aria-live="polite" data-testid="reconcile-totals">
      <dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div>
          <dt className="text-sm text-muted">{t("expected")}</dt>
          <dd className="text-lg font-bold tabular-nums text-strong" data-testid="sum-expected">{money(totals.expected)}</dd>
        </div>
        <div>
          <dt className="text-sm text-muted">{t("received")}</dt>
          <dd className="text-lg font-bold tabular-nums text-strong" data-testid="sum-received">{money(totals.received)}</dd>
        </div>
        <div>
          <dt className="text-sm text-muted">{t("difference")}</dt>
          <dd className="text-lg" data-testid="sum-difference"><Difference difference={totals.difference} /></dd>
        </div>
        <div>
          <dt className="text-sm text-muted">{t("shortOver")}</dt>
          <dd className="text-lg font-bold tabular-nums text-strong" data-testid="sum-counts">{t("shortOverValue", { short: totals.short, over: totals.over })}</dd>
        </div>
      </dl>
    </div>
  );
}

const ReconcileRow = React.memo(function ReconcileRow({ item, store, attempted }: { item: SettlementItem; store: Store; attempted: boolean }) {
  const t = useTranslations("cod.reconcile");
  const money = useMoney2();
  const initial = store.values.get(item.shipmentId)!;
  const [received, setReceived] = React.useState(initial.received);
  const [note, setNote] = React.useState(initial.note);
  const expected = num(item.expected);
  const check = checkLine({ shipmentId: item.shipmentId, expected, received, note });
  const amountError = (attempted || received !== initial.received) && check.difference === null ? t("amountInvalid") : undefined;
  const noteError = attempted && check.noteMissing ? t("noteRequired") : attempted && check.noteTooLong ? t("noteTooLong", { max: NOTE_MAX }) : undefined;

  return (
    <li className="grid gap-3 rounded-card border border-line bg-card p-3 md:grid-cols-[minmax(0,1.1fr)_8rem_10rem_9rem_minmax(0,1.4fr)] md:items-start" data-testid="reconcile-row" data-consignment={item.consignmentId}>
      <div className="min-w-0">
        <p className="font-mono text-sm font-semibold text-strong [overflow-wrap:anywhere]">{item.consignmentId}</p>
        <Link href={`/admin/orders/${item.orderId}`} className="inline-flex min-h-11 items-center rounded-control font-mono text-sm text-primary hover:underline">
          {item.orderNumber}
        </Link>
      </div>
      <div>
        <p className="text-sm font-medium text-strong">{t("expected")}</p>
        <p className="flex min-h-11 items-center tabular-nums" data-testid="row-expected">{money(expected)}</p>
      </div>
      <Field label={t("received")} htmlFor={`recv-${item.shipmentId}`} error={amountError}>
        <Input
          id={`recv-${item.shipmentId}`}
          inputMode="decimal"
          autoComplete="off"
          value={received}
          aria-invalid={!!amountError}
          aria-label={t("receivedFor", { consignment: item.consignmentId })}
          className="tabular-nums"
          onChange={(e) => {
            setReceived(e.target.value);
            store.set(item.shipmentId, { received: e.target.value, note });
          }}
        />
      </Field>
      <div>
        <p className="text-sm font-medium text-strong">{t("difference")}</p>
        <p className="flex min-h-11 items-center" data-testid="row-difference">
          {check.difference === null ? <span className="text-muted">-</span> : <Difference difference={check.difference} />}
        </p>
      </div>
      <Field label={check.variance && check.variance !== "exact" ? t("noteRequiredLabel") : t("noteOptional")} htmlFor={`note-${item.shipmentId}`} error={noteError}>
        <Input
          id={`note-${item.shipmentId}`}
          autoComplete="off"
          maxLength={NOTE_MAX}
          value={note}
          aria-invalid={!!noteError}
          aria-label={t("noteFor", { consignment: item.consignmentId })}
          aria-required={!!check.variance && check.variance !== "exact"}
          onChange={(e) => {
            setNote(e.target.value);
            store.set(item.shipmentId, { received, note: e.target.value });
          }}
        />
      </Field>
    </li>
  );
});
