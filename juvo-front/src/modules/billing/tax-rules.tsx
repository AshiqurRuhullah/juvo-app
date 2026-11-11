"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Pencil, Plus, Power, PowerOff, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, ChoiceRow, Field, Input, Label, Switch, Textarea } from "@/components/ui/form";
import { Dialog, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { useTableLabels } from "@/components/data/list-kit";
import { toNumber } from "@/modules/catalog/format";
import { usePaymentPermissions } from "@/modules/payments/permissions";
import { useActivateTaxRule, useCreateTaxRule, useDeactivateTaxRule, useTaxRules, useUpdateTaxRule, type TaxRule } from "./api";
import { useReportBillingError } from "./common";

type DialogState = { kind: "new" } | { kind: "edit"; rule: TaxRule } | null;

/** The tax rules: a short list (a handful of rows), a form dialog, and activation that asks for an acknowledgement of unverified rules. */
export function TaxRules() {
  const t = useTranslations("billing.rules");
  const tc = useTranslations("billing.common");
  const labels = useTableLabels();
  const report = useReportBillingError();
  const perms = usePaymentPermissions();
  const query = useTaxRules();
  const activate = useActivateTaxRule();
  const deactivate = useDeactivateTaxRule();
  const [dialog, setDialog] = React.useState<DialogState>(null);
  const [activating, setActivating] = React.useState<TaxRule | null>(null);
  const [deactivating, setDeactivating] = React.useState<TaxRule | null>(null);
  const [ack, setAck] = React.useState(false);
  const [ackError, setAckError] = React.useState(false);

  const rules = React.useMemo(() => query.data ?? [], [query.data]);

  const columns = React.useMemo<ColumnDef<TaxRule, unknown>[]>(
    () => [
      {
        id: "name",
        header: t("columns.name"),
        enableSorting: false,
        meta: { primary: true },
        cell: ({ row }) => (
          <span className="inline-flex min-h-11 flex-col justify-center">
            <span className="font-semibold text-strong [overflow-wrap:anywhere]">{row.original.name}</span>
            <span className="font-mono text-sm text-muted">{row.original.code}</span>
          </span>
        ),
      },
      { id: "rate", header: t("columns.rate"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => <span className="tabular-nums">{t("rate", { rate: toNumber(row.original.ratePercent) })}</span> },
      { id: "delivery", header: t("columns.delivery"), enableSorting: false, meta: { secondary: true }, cell: ({ row }) => (row.original.appliesToDelivery ? t("appliesToDelivery") : t("deliveryNo")) },
      {
        id: "verified",
        header: t("columns.verified"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) =>
          row.original.isVerified ? (
            <Badge variant="success">{t("verified")}</Badge>
          ) : (
            <Badge variant="warning">
              <TriangleAlert className="size-3.5" aria-hidden /> {t("unverified")}
            </Badge>
          ),
      },
      {
        id: "status",
        header: t("columns.status"),
        enableSorting: false,
        meta: { secondary: true },
        cell: ({ row }) => <Badge variant={row.original.isActive ? "success" : "outline"}>{row.original.isActive ? t("active") : t("inactive")}</Badge>,
      },
      {
        id: "actions",
        header: "",
        meta: { actions: true, align: "end", label: t("columns.actions") },
        cell: ({ row }) => {
          const rule = row.original;
          if (!perms.taxRuleEdit) return null;
          return (
            <span className="flex justify-end gap-1">
              <Button variant="ghost" size="icon" aria-label={t("edit", { name: rule.name })} onClick={() => setDialog({ kind: "edit", rule })}>
                <Pencil aria-hidden />
              </Button>
              {rule.isActive ? (
                <Button variant="ghost" size="icon" aria-label={t("deactivate", { name: rule.name })} onClick={() => setDeactivating(rule)} data-testid={`deactivate-${rule.code}`}>
                  <PowerOff aria-hidden />
                </Button>
              ) : (
                <Button
                  variant="ghost"
                  size="icon"
                  aria-label={t("activate", { name: rule.name })}
                  onClick={() => {
                    setAck(false);
                    setAckError(false);
                    setActivating(rule);
                  }}
                  data-testid={`activate-${rule.code}`}
                >
                  <Power aria-hidden />
                </Button>
              )}
            </span>
          );
        },
      },
    ],
    [t, perms.taxRuleEdit],
  );

  const confirmActivate = async () => {
    if (!activating) return;
    if (!activating.isVerified && !ack) return setAckError(true);
    try {
      await activate.mutateAsync({ id: activating.id, acknowledgeUnverified: activating.isVerified ? false : ack });
      toast.success(t("activated"));
      setActivating(null);
    } catch (error) {
      report(error);
      setActivating(null);
    }
  };

  const confirmDeactivate = async () => {
    if (!deactivating) return;
    try {
      await deactivate.mutateAsync(deactivating.id);
      toast.success(t("deactivated"));
    } catch (error) {
      report(error);
    }
    setDeactivating(null);
  };

  return (
    <div className="space-y-4">
      <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <p className="max-w-2xl text-muted">{t("intro")}</p>
        {perms.taxRuleEdit ? (
          <Button onClick={() => setDialog({ kind: "new" })} data-testid="new-rule">
            <Plus aria-hidden /> {t("add")}
          </Button>
        ) : null}
      </div>
      {query.isError ? <Alert variant="danger">{tc("loadFailed")}</Alert> : null}
      <DataTable caption={t("searchLabel")} columns={columns} data={rules} loading={query.isLoading} getRowId={(r) => r.id} labels={labels} page={1} pageSize={Math.max(rules.length, 1)} total={rules.length} onPageChange={() => undefined} sorting={[]} onSortingChange={() => undefined} />

      <Dialog open={dialog !== null} onOpenChange={(open) => !open && setDialog(null)}>
        <ModalContent size="md" closeLabel={tc("close")}>
          {dialog ? <RuleForm key={dialog.kind === "edit" ? dialog.rule.id : "new"} rule={dialog.kind === "edit" ? dialog.rule : undefined} onDone={() => setDialog(null)} /> : null}
        </ModalContent>
      </Dialog>

      <ConfirmDialog
        open={activating !== null}
        onOpenChange={(open) => !open && setActivating(null)}
        title={t("activateTitle")}
        description={activating ? t("activateText", { name: activating.name, rate: toNumber(activating.ratePercent) }) : ""}
        confirmLabel={t("activateConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        loading={activate.isPending}
        onConfirm={confirmActivate}
      >
        {activating && !activating.isVerified ? (
          <div className="space-y-3">
            <Alert variant="warning" title={t("unverifiedTitle")} data-testid="unverified-warning">
              {t("unverifiedText")}
            </Alert>
            <div className="flex min-h-11 items-start gap-3">
              <Checkbox id="rule-ack" checked={ack} onCheckedChange={(v) => { setAck(v === true); setAckError(false); }} aria-invalid={ackError} className="mt-0.5" />
              <Label htmlFor="rule-ack" className="flex min-h-11 flex-1 cursor-pointer items-center font-normal">
                {t("acknowledge")}
              </Label>
            </div>
            {ackError ? (
              <p role="alert" className="text-sm font-medium text-danger">
                {t("acknowledgeRequired")}
              </p>
            ) : null}
          </div>
        ) : null}
      </ConfirmDialog>

      <ConfirmDialog
        open={deactivating !== null}
        onOpenChange={(open) => !open && setDeactivating(null)}
        title={t("deactivateTitle")}
        description={t("deactivateText")}
        confirmLabel={t("deactivateConfirm")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={deactivate.isPending}
        onConfirm={confirmDeactivate}
      />
    </div>
  );
}

function RuleForm({ rule, onDone }: { rule?: TaxRule; onDone: () => void }) {
  const t = useTranslations("billing.rules");
  const tc = useTranslations("billing.common");
  const report = useReportBillingError();
  const create = useCreateTaxRule();
  const update = useUpdateTaxRule();
  const [code, setCode] = React.useState(rule?.code ?? "");
  const [name, setName] = React.useState(rule?.name ?? "");
  const [rate, setRate] = React.useState(rule ? String(toNumber(rule.ratePercent)) : "");
  const [delivery, setDelivery] = React.useState(rule?.appliesToDelivery ?? false);
  const [verified, setVerified] = React.useState(rule?.isVerified ?? false);
  const [note, setNote] = React.useState(rule?.note ?? "");
  const [errors, setErrors] = React.useState<Record<string, string>>({});

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    const next: Record<string, string> = {};
    const value = Number(rate);
    if (!code.trim()) next.code = t("errors.code");
    if (!name.trim()) next.name = t("errors.name");
    if (rate.trim() === "" || !Number.isFinite(value) || value < 0 || value > 100) next.ratePercent = t("errors.rate");
    setErrors(next);
    if (Object.keys(next).length) return;
    const body = { code: code.trim(), name: name.trim(), ratePercent: value, isInclusive: true, appliesToDelivery: delivery, isVerified: verified, note: note.trim() || null };
    try {
      if (rule) await update.mutateAsync({ id: rule.id, body });
      else await create.mutateAsync(body);
      toast.success(rule ? tc("saved") : tc("created"));
      onDone();
    } catch (error) {
      setErrors(report(error));
    }
  };

  return (
    <form onSubmit={submit} noValidate className="space-y-5">
      <div>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{rule ? t("dialogEdit") : t("dialogNew")}</DialogTitle>
        <DialogDescription className="mt-1 text-muted">{t("inclusiveNote")}</DialogDescription>
      </div>
      <div className="space-y-4">
        <Field label={t("code")} htmlFor="rule-code" required hint={t("codeHint")} error={errors.code}>
          <Input id="rule-code" value={code} onChange={(e) => setCode(e.target.value)} disabled={!!rule} autoComplete="off" autoCapitalize="characters" aria-invalid={!!errors.code} />
        </Field>
        <Field label={t("name")} htmlFor="rule-name" required error={errors.name}>
          <Input id="rule-name" value={name} onChange={(e) => setName(e.target.value)} autoComplete="off" aria-invalid={!!errors.name} />
        </Field>
        <Field label={t("ratePercent")} htmlFor="rule-rate" required hint={t("rateHint")} error={errors.ratePercent}>
          <Input id="rule-rate" type="number" inputMode="decimal" step="0.01" min="0" max="100" value={rate} onChange={(e) => setRate(e.target.value)} aria-invalid={!!errors.ratePercent} />
        </Field>
        <ChoiceRow htmlFor="rule-delivery">
          <Switch id="rule-delivery" checked={delivery} onCheckedChange={setDelivery} />
          <span>
            <span className="block font-medium">{t("deliveryLabel")}</span>
            <span className="block text-sm text-muted">{t("deliveryHint")}</span>
          </span>
        </ChoiceRow>
        <ChoiceRow htmlFor="rule-verified">
          <Switch id="rule-verified" checked={verified} onCheckedChange={setVerified} />
          <span>
            <span className="block font-medium">{t("verifiedLabel")}</span>
            <span className="block text-sm text-muted">{t("verifiedHint")}</span>
          </span>
        </ChoiceRow>
        <Field label={t("note")} htmlFor="rule-note" error={errors.note}>
          <Textarea id="rule-note" rows={3} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
      </div>
      <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
        <Button type="button" variant="secondary" onClick={onDone}>
          {tc("cancel")}
        </Button>
        <Button type="submit" loading={create.isPending || update.isPending}>
          {tc("save")}
        </Button>
      </div>
    </form>
  );
}
