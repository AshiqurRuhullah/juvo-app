"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import { Alert, Card, Skeleton } from "@/components/ui/surfaces";
import { Field, Input } from "@/components/ui/form";
import { Button } from "@/components/ui/button";
import { FilterSelect } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useBranchOptions } from "@/modules/inventory/common";
import { dayRange } from "@/modules/orders/status";
import { useLedger } from "./api";
import { usePayMethodLabel } from "./common";

function Tile({ label, value, hint, testid, tone }: { label: string; value: string; hint?: string; testid: string; tone?: "danger" }) {
  return (
    <Card className="space-y-1 p-4">
      <p className="text-sm text-muted">{label}</p>
      <p className={tone === "danger" ? "text-2xl font-semibold tabular-nums text-danger-ink" : "text-2xl font-semibold tabular-nums text-strong"} data-testid={testid}>
        {value}
      </p>
      {hint ? <p className="text-sm text-muted">{hint}</p> : null}
    </Card>
  );
}

/** Money in and out for a date range, per method, with what is still waiting (cash not settled, transfers under review, pending refunds). */
export function LedgerView() {
  const t = useTranslations("payments.admin.ledger");
  const tc = useTranslations("payments.admin.common");
  const money = useMoney();
  const methodLabel = usePayMethodLabel();
  const branches = useBranchOptions();
  const [from, setFrom] = React.useState("");
  const [to, setTo] = React.useState("");
  const [branchId, setBranchId] = React.useState<string | undefined>(undefined);
  const range = dayRange(from || undefined, to || undefined);
  const query = useLedger({ ...range, branchId });
  const d = query.data;

  return (
    <div className="space-y-5">
      <div className="grid gap-3 sm:grid-cols-3">
        <Field label={t("from")} htmlFor="ledger-from" hint={t("rangeHint")}>
          <Input id="ledger-from" type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} />
        </Field>
        <Field label={t("to")} htmlFor="ledger-to">
          <Input id="ledger-to" type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} />
        </Field>
        {branches.length > 1 ? (
          <div className="self-end">
            <FilterSelect value={branchId} onChange={setBranchId} options={branches} allLabel={t("allBranches")} label={t("branch")} searchPlaceholder={tc("search")} emptyText={tc("noMatch")} />
          </div>
        ) : null}
      </div>
      {from || to || branchId ? (
        <div>
          <Button
            variant="ghost"
            onClick={() => {
              setFrom("");
              setTo("");
              setBranchId(undefined);
            }}
          >
            {tc("clear")}
          </Button>
        </div>
      ) : null}

      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      {!d ? (
        <Skeleton className="h-48" aria-busy />
      ) : (
        <>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4" data-testid="ledger-tiles">
            <Tile label={t("onlineReceived")} value={money(d.onlineReceived)} testid="ledger-online-received" />
            <Tile label={t("onlineRefunded")} value={money(d.onlineRefunded)} testid="ledger-online-refunded" />
            <Tile label={t("codCollected")} value={money(d.codCollected)} testid="ledger-cod-collected" />
            <Tile label={t("codSettled")} value={money(d.codSettled)} testid="ledger-cod-settled" />
            <Tile label={t("codOutstanding")} value={money(d.codOutstanding)} hint={t("codOutstandingHint")} testid="ledger-cod-outstanding" />
            <Tile label={t("underReview")} value={money(d.underReviewAmount)} hint={t("count", { count: toNumber(d.underReviewCount) })} testid="ledger-under-review" />
            <Tile label={t("pendingRefunds")} value={money(d.pendingRefundAmount)} hint={t("count", { count: toNumber(d.pendingRefundCount) })} testid="ledger-pending-refunds" />
            <Tile label={t("net")} value={money(d.net)} hint={t("netHint")} testid="ledger-net" />
          </div>

          <section aria-labelledby="ledger-methods" className="space-y-2">
            <h2 id="ledger-methods" className="text-lg font-semibold text-strong">
              {t("byMethod")}
            </h2>
            {d.byMethod.length === 0 ? (
              <p className="text-muted">{t("noMethods")}</p>
            ) : (
              <ul className="grid gap-3 md:grid-cols-2 xl:grid-cols-3" data-testid="ledger-methods">
                {d.byMethod.map((m) => (
                  <li key={m.method} className="rounded-card border border-line bg-card p-4">
                    <p className="font-semibold text-strong">{methodLabel(m.method)}</p>
                    <dl className="mt-2 space-y-1 text-sm">
                      <div className="flex justify-between gap-3">
                        <dt className="text-muted">{t("payments")}</dt>
                        <dd className="tabular-nums text-strong">{toNumber(m.payments)}</dd>
                      </div>
                      <div className="flex justify-between gap-3">
                        <dt className="text-muted">{t("received")}</dt>
                        <dd className="tabular-nums text-strong">{money(m.received)}</dd>
                      </div>
                      <div className="flex justify-between gap-3">
                        <dt className="text-muted">{t("refunded")}</dt>
                        <dd className="tabular-nums text-strong">{money(m.refunded)}</dd>
                      </div>
                    </dl>
                  </li>
                ))}
              </ul>
            )}
          </section>
        </>
      )}
    </div>
  );
}
