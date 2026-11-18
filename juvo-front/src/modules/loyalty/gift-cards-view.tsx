"use client";

import * as React from "react";
import { useTranslations } from "next-intl";
import type { ColumnDef } from "@tanstack/react-table";
import { Copy, Eye, Plus, Ban, TriangleAlert } from "lucide-react";
import { Button } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { DataTable } from "@/components/ui/data-table";
import { Checkbox, Field, Input, Label, Textarea } from "@/components/ui/form";
import { Dialog, DialogClose, DialogDescription, DialogTitle, ModalContent } from "@/components/ui/overlays";
import { Alert, Badge, Skeleton } from "@/components/ui/surfaces";
import { toast } from "@/components/ui/toaster";
import { FilterSelect, useDateTime, useListState, useTableLabels } from "@/components/data/list-kit";
import { toNumber, useMoney } from "@/modules/catalog/format";
import { useCreateGiftCard, useDeactivateGiftCard, useGiftCardDetail, useGiftCards, useLoyaltySettings, type GiftCard, type GiftCardCreated } from "./api";
import { useLoyaltyErrorText, useLoyaltyPermissions } from "./common";
import { DialogForm } from "./dialog-form";
import { formatGiftCardCode, giftCardStatus, GIFT_CARD_STATUSES, validateGiftCard, type GiftCardDraft, type GiftCardStatus } from "./logic";

const TONE: Record<GiftCardStatus, "success" | "neutral" | "warning" | "danger"> = { active: "success", used: "neutral", expired: "warning", deactivated: "danger" };

function StatusBadge({ status }: { status: string }) {
  const t = useTranslations("loyalty.cards.status");
  const known = giftCardStatus(status);
  if (!known) return <Badge variant="outline">{status}</Badge>;
  return (
    <Badge variant={TONE[known]}>
      {known === "deactivated" ? <Ban className="size-3.5" aria-hidden /> : null}
      {t(known)}
    </Badge>
  );
}

const localToday = () => {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
};

const emptyDraft: GiftCardDraft = { amount: "", expiresOn: "", note: "", recipientMobile: "", recipientEmail: "", send: false };

/** Staff `Gift cards` tab: list by status, create (the code is shown once), details with the card's entries, deactivate (FR-AB03). */
export default function GiftCardsView() {
  const t = useTranslations("loyalty.cards");
  const tc = useTranslations("loyalty.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const labels = useTableLabels();
  const perms = useLoyaltyPermissions();
  const state = useListState<{ status?: string }>({ status: undefined });
  const query = useGiftCards({ page: state.page, pageSize: state.pageSize, status: state.filters.status });
  const items = React.useMemo(() => query.data?.items ?? [], [query.data]);
  const [creating, setCreating] = React.useState(false);
  const [created, setCreated] = React.useState<GiftCardCreated | null>(null);
  const [detailId, setDetailId] = React.useState<string | null>(null);
  const [deactivating, setDeactivating] = React.useState<GiftCard | null>(null);
  const deactivate = useDeactivateGiftCard();
  const errorText = useLoyaltyErrorText();

  const columns = React.useMemo<ColumnDef<GiftCard, unknown>[]>(
    () => [
      { id: "code", header: t("columns.code"), meta: { primary: true }, cell: ({ row }) => <span className="font-mono font-semibold text-strong">{`••••-${row.original.codeHint}`}</span> },
      { id: "status", header: t("columns.status"), meta: { secondary: true }, cell: ({ row }) => <StatusBadge status={row.original.status} /> },
      { id: "balance", header: t("columns.balance"), meta: { align: "end", secondary: true }, cell: ({ row }) => <span className="font-semibold tabular-nums text-strong">{money(row.original.balance)}</span> },
      { id: "initial", header: t("columns.initial"), meta: { align: "end", hideOnMobile: true }, cell: ({ row }) => <span className="tabular-nums text-muted">{money(row.original.initialAmount)}</span> },
      { id: "expires", header: t("columns.expires"), meta: { secondary: true }, cell: ({ row }) => <span className="text-muted">{row.original.expiresAt ? dateTime(row.original.expiresAt) : t("never")}</span> },
      { id: "note", header: t("columns.note"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-sm text-muted [overflow-wrap:anywhere]">{row.original.note ?? "-"}</span> },
      { id: "by", header: t("columns.by"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-sm text-muted">{row.original.createdByName ?? "-"}</span> },
      { id: "created", header: t("columns.created"), meta: { hideOnMobile: true }, cell: ({ row }) => <span className="text-muted">{dateTime(row.original.createdAt)}</span> },
      {
        id: "actions",
        header: t("columns.actions"),
        meta: { actions: true },
        cell: ({ row }) => (
          <div className="flex flex-wrap gap-2">
            <Button variant="secondary" size="md" onClick={() => setDetailId(row.original.id)} data-testid="card-details">
              <Eye aria-hidden /> {t("details")}
            </Button>
            {perms.cardCreate && giftCardStatus(row.original.status) === "active" ? (
              <Button variant="secondary" size="md" onClick={() => setDeactivating(row.original)} data-testid="card-deactivate">
                <Ban aria-hidden /> {t("deactivate")}
              </Button>
            ) : null}
          </div>
        ),
      },
    ],
    [t, money, dateTime, perms.cardCreate],
  );

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div className="w-full sm:w-64">
          <FilterSelect
            value={state.filters.status}
            onChange={(v) => state.setFilter("status", v)}
            options={GIFT_CARD_STATUSES.map((s) => ({ value: s, label: t(`status.${s}`) }))}
            allLabel={t("anyStatus")}
            label={t("columns.status")}
            searchPlaceholder={tc("search")}
            emptyText={tc("noMatch")}
          />
        </div>
        {perms.cardCreate ? (
          <Button onClick={() => setCreating(true)} data-testid="card-create">
            <Plus aria-hidden /> {t("create.open")}
          </Button>
        ) : null}
      </div>
      {query.isError ? <Alert variant="danger">{t("loadFailed")}</Alert> : null}
      <DataTable caption={t("title")} columns={columns} data={items} loading={query.isLoading} getRowId={(x) => x.id} labels={labels} page={state.page} pageSize={state.pageSize} total={toNumber(query.data?.total)} onPageChange={state.setPage} />

      {creating ? (
        <CreateDialog
          onClose={() => setCreating(false)}
          onCreated={(result) => {
            setCreating(false);
            setCreated(result);
          }}
        />
      ) : null}
      {created ? <CodeOnceDialog result={created} onClose={() => setCreated(null)} /> : null}
      {detailId ? <DetailDialog id={detailId} onClose={() => setDetailId(null)} /> : null}
      <ConfirmDialog
        open={!!deactivating}
        onOpenChange={(open) => !open && setDeactivating(null)}
        title={t("deactivateDialog.title")}
        description={deactivating ? t("deactivateDialog.text", { hint: deactivating.codeHint, balance: money(deactivating.balance) }) : ""}
        confirmLabel={t("deactivate")}
        cancelLabel={tc("cancel")}
        closeLabel={tc("close")}
        destructive
        loading={deactivate.isPending}
        onConfirm={async () => {
          if (!deactivating) return;
          try {
            await deactivate.mutateAsync(deactivating.id);
            toast.success(t("deactivated"));
          } catch (failure) {
            toast.error(errorText(failure));
          }
          setDeactivating(null);
        }}
      />
    </div>
  );
}

function CreateDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (result: GiftCardCreated) => void }) {
  const t = useTranslations("loyalty.cards.create");
  const tc = useTranslations("loyalty.common");
  const money = useMoney();
  const settings = useLoyaltySettings();
  const create = useCreateGiftCard();
  const errorText = useLoyaltyErrorText();
  const [draft, setDraft] = React.useState<GiftCardDraft>(emptyDraft);
  const [shown, setShown] = React.useState(false);
  const [failure, setFailure] = React.useState<string | null>(null);
  const limits = settings.data ? { min: toNumber(settings.data.giftCardMinAmount), max: toNumber(settings.data.giftCardMaxAmount) } : null;
  const errors = validateGiftCard(draft, limits, localToday());
  const set = <K extends keyof GiftCardDraft>(key: K, value: GiftCardDraft[K]) => setDraft((old) => ({ ...old, [key]: value }));

  const submit = async () => {
    setShown(true);
    setFailure(null);
    if (Object.keys(errors).length > 0) return;
    try {
      onCreated(await create.mutateAsync(draft));
    } catch (error) {
      setFailure(errorText(error));
    }
  };

  return (
    <DialogForm open onOpenChange={(o) => !o && onClose()} title={t("title")} description={t("text")} submitLabel={t("submit")} cancelLabel={tc("cancel")} closeLabel={tc("close")} loading={create.isPending} error={failure} onSubmit={() => void submit()}>
      <Field label={t("amount")} htmlFor="gc-amount" required hint={limits ? t("amountHint", { min: money(limits.min), max: money(limits.max) }) : undefined} error={shown && errors.amount ? t(`errors.${errors.amount}`, limits ? { min: money(limits.min), max: money(limits.max) } : {}) : undefined}>
        <Input id="gc-amount" inputMode="decimal" value={draft.amount} onChange={(e) => set("amount", e.target.value)} aria-invalid={!!(shown && errors.amount)} autoComplete="off" data-testid="card-amount" />
      </Field>
      <Field label={t("expires")} htmlFor="gc-expires" hint={t("expiresHint")} error={shown && errors.expiresOn ? t("errors.past") : undefined}>
        <Input id="gc-expires" type="date" min={localToday()} value={draft.expiresOn} onChange={(e) => set("expiresOn", e.target.value)} data-testid="card-expires" />
      </Field>
      <Field label={t("note")} htmlFor="gc-note">
        <Textarea id="gc-note" rows={2} maxLength={200} value={draft.note} onChange={(e) => set("note", e.target.value)} data-testid="card-note" />
      </Field>
      <div className="grid gap-4 sm:grid-cols-2">
        <Field label={t("mobile")} htmlFor="gc-mobile">
          <Input id="gc-mobile" type="tel" inputMode="tel" value={draft.recipientMobile} onChange={(e) => set("recipientMobile", e.target.value)} autoComplete="off" data-testid="card-mobile" />
        </Field>
        <Field label={t("email")} htmlFor="gc-email">
          <Input id="gc-email" type="email" inputMode="email" value={draft.recipientEmail} onChange={(e) => set("recipientEmail", e.target.value)} autoComplete="off" />
        </Field>
      </div>
      <div className="flex min-h-11 items-center gap-3">
        <Checkbox id="gc-send" checked={draft.send} onCheckedChange={(v) => set("send", v === true)} data-testid="card-send" />
        <Label htmlFor="gc-send" className="flex min-h-11 flex-1 cursor-pointer items-center font-normal">
          {t("send")}
        </Label>
      </div>
      {draft.send ? <p className="text-sm text-muted">{t("sendNote")}</p> : null}
      {shown && errors.recipient ? (
        <p role="alert" className="text-sm font-medium text-danger">
          {t("errors.needsRecipient")}
        </p>
      ) : null}
    </DialogForm>
  );
}

/** The code appears here once, right after creation, and is gone when the dialog closes: the API never returns it again. */
function CodeOnceDialog({ result, onClose }: { result: GiftCardCreated; onClose: () => void }) {
  const t = useTranslations("loyalty.cards.once");
  const tc = useTranslations("loyalty.common");
  const money = useMoney();
  const code = formatGiftCardCode(result.code);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(code);
      toast.success(t("copied"));
    } catch {
      toast.error(t("copyFailed"));
    }
  };
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <ModalContent size="sm" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
        <DialogDescription className="mt-2 text-muted">{t("text", { amount: money(result.card.initialAmount) })}</DialogDescription>
        <div className="mt-4 space-y-4">
          <p className="rounded-control border border-line bg-sunken p-4 text-center font-mono text-xl font-semibold tracking-wider text-strong [overflow-wrap:anywhere]" data-testid="card-code">
            {code}
          </p>
          <Alert variant="warning" title={t("warnTitle")}>
            <TriangleAlert className="me-1 inline size-4" aria-hidden /> {t("warn")}
          </Alert>
          <div className="flex flex-col-reverse gap-2 sm:flex-row sm:justify-end">
            <DialogClose asChild>
              <Button variant="secondary" data-testid="card-code-done">
                {t("done")}
              </Button>
            </DialogClose>
            <Button onClick={() => void copy()} data-testid="card-code-copy">
              <Copy aria-hidden /> {t("copy")}
            </Button>
          </div>
        </div>
      </ModalContent>
    </Dialog>
  );
}

function DetailDialog({ id, onClose }: { id: string; onClose: () => void }) {
  const t = useTranslations("loyalty.cards.detail");
  const tk = useTranslations("loyalty.cards.entryKind");
  const tc = useTranslations("loyalty.common");
  const money = useMoney();
  const dateTime = useDateTime();
  const query = useGiftCardDetail(id);
  const card = query.data?.card;
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <ModalContent size="md" closeLabel={tc("close")}>
        <DialogTitle className="font-display text-xl font-semibold text-strong">{t("title")}</DialogTitle>
        <DialogDescription className="sr-only">{t("title")}</DialogDescription>
        {query.isPending ? <Skeleton className="mt-4 h-40" aria-busy /> : query.isError || !card ? <Alert variant="danger" className="mt-4">{t("loadFailed")}</Alert> : (
          <div className="mt-4 space-y-4" data-testid="card-detail">
            <dl className="grid gap-2 text-sm sm:grid-cols-2">
              <div>
                <dt className="text-muted">{t("code")}</dt>
                <dd className="font-mono font-semibold text-strong">{`••••-${card.codeHint}`}</dd>
              </div>
              <div>
                <dt className="text-muted">{t("status")}</dt>
                <dd>
                  <StatusBadge status={card.status} />
                </dd>
              </div>
              <div>
                <dt className="text-muted">{t("balance")}</dt>
                <dd className="font-semibold text-strong">
                  {money(card.balance)} <span className="font-normal text-muted">/ {money(card.initialAmount)}</span>
                </dd>
              </div>
              <div>
                <dt className="text-muted">{t("expires")}</dt>
                <dd className="text-strong">{card.expiresAt ? dateTime(card.expiresAt) : t("never")}</dd>
              </div>
              {card.recipientMobile ? (
                <div>
                  <dt className="text-muted">{t("recipient")}</dt>
                  <dd className="text-strong">{card.recipientMobile}</dd>
                </div>
              ) : null}
              {card.note ? (
                <div className="sm:col-span-2">
                  <dt className="text-muted">{t("note")}</dt>
                  <dd className="text-strong [overflow-wrap:anywhere]">{card.note}</dd>
                </div>
              ) : null}
            </dl>
            <p className="text-sm text-muted">{t("codeHidden")}</p>
            <h3 className="font-semibold text-strong">{t("entries")}</h3>
            <ul className="divide-y divide-line text-sm" data-testid="card-entries">
              {query.data!.entries.map((e) => (
                <li key={e.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                  <span className="min-w-0">
                    <span className="font-medium text-strong">{tk(e.kind)}</span>
                    <span className="block text-muted">
                      {dateTime(e.at)}
                      {e.orderNumber ? ` · ${e.orderNumber}` : ""}
                      {e.actorName ? ` · ${e.actorName}` : ""}
                    </span>
                  </span>
                  <span className="text-end tabular-nums">
                    <span className="block font-semibold text-strong">{money(e.amount)}</span>
                    <span className="block text-muted">{t("after", { balance: money(e.balanceAfter) })}</span>
                  </span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </ModalContent>
    </Dialog>
  );
}
